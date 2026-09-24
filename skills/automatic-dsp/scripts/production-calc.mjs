import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

function number(value, name, min = 0, inclusive = true) {
  if (typeof value !== 'number' || !Number.isFinite(value) ||
      (inclusive ? value < min : value <= min)) {
    throw new Error(`${name} 必须是${inclusive ? '大于等于' : '大于'} ${min} 的有限数值`);
  }
  return value;
}

function rates(value, name) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${name} 必须是物品 ID 到数量的对象`);
  }
  for (const [key, amount] of Object.entries(value)) number(amount, `${name}.${key}`);
  return value;
}

function add(into, from, multiplier = 1) {
  for (const [item, value] of Object.entries(from)) {
    into[item] = (into[item] ?? 0) + value * multiplier;
  }
}

function scaled(values, multiplier) {
  const result = Object.create(null);
  add(result, values, multiplier);
  return result;
}

// 输入为归一化后的游戏数据；配方适用性由调用者根据当前游戏核实。
function model(recipe) {
  const time = number(recipe.timeSeconds, 'timeSeconds', 0, false);
  const speed = number(recipe.machineSpeed, 'machineSpeed', 0, false);
  const inputs = { ...rates(recipe.inputs, 'inputs') };
  const outputs = { ...rates(recipe.outputs, 'outputs') };
  if (!Object.values(outputs).some(value => value > 0)) throw new Error('配方需要正产出');
  let yieldMultiplier = 1;
  let speedMultiplier = 1;
  let powerMultiplier = 1;
  const spray = recipe.proliferation;
  if (spray) {
    if (!['extra', 'speed'].includes(spray.mode)) throw new Error('mode 应为 extra 或 speed');
    const bonus = number(spray.bonus, 'bonus', 0, false);
    if (spray.mode === 'extra') yieldMultiplier += bonus;
    else speedMultiplier += bonus;
    powerMultiplier = number(spray.powerMultiplier, 'powerMultiplier', 1);
    if (typeof spray.item !== 'string' || !spray.item) throw new Error('增产剂需要物品 ID');
    const sprays = number(spray.spraysPerItem, 'spraysPerItem', 0, false);
    if (spray.selfSpray !== undefined && typeof spray.selfSpray !== 'boolean') {
      throw new Error('selfSpray 必须是布尔值');
    }
    const netSprays = sprays - (spray.selfSpray ? 1 : 0);
    if (netSprays <= 0) throw new Error('扣除自喷后可用喷涂次数必须大于零');
    // 默认所有输入都需喷涂；已喷涂输入可显式提供本工序新增喷涂数量。
    const sprayed = spray.sprayedInputs ?? recipe.inputs;
    rates(sprayed, 'sprayedInputs');
    for (const [item, count] of Object.entries(sprayed)) {
      if (!(item in recipe.inputs) || count > recipe.inputs[item]) {
        throw new Error(`sprayedInputs.${item} 超过配方输入`);
      }
    }
    const count = Object.values(sprayed).reduce((sum, value) => sum + value, 0);
    inputs[spray.item] = (inputs[spray.item] ?? 0) + count / netSprays;
  }
  return {
    inputs, outputs: scaled(outputs, yieldMultiplier),
    cyclesPerMachine: speed * speedMultiplier / time,
    activePowerMW: recipe.activePowerMW === undefined ? null
      : number(recipe.activePowerMW, 'activePowerMW') * powerMultiplier,
  };
}

function atCycles(recipe, cyclesPerSecond) {
  number(cyclesPerSecond, 'cyclesPerSecond');
  const data = model(recipe);
  const machinesExact = cyclesPerSecond / data.cyclesPerMachine;
  // 直接向上取整，保留真实的小数需求；显示精度不参与计算。
  const machines = Math.ceil(machinesExact);
  const fullCycles = machines * data.cyclesPerMachine;
  const flow = cycles => ({
    inputs: scaled(data.inputs, cycles), outputs: scaled(data.outputs, cycles),
  });
  return {
    cyclesPerSecond, machinesExact, machines,
    target: flow(cyclesPerSecond), fullLoad: flow(fullCycles),
    fullLoadPowerMW: data.activePowerMW === null ? null : machines * data.activePowerMW,
  };
}

export function calculateRecipe(input) {
  const data = model(input.recipe);
  const output = data.outputs[input.targetItem];
  if (!(output > 0)) throw new Error('targetItem 必须是配方产物');
  const target = number(input.targetPerSecond, 'targetPerSecond');
  return atCycles(input.recipe, target / output);
}

// 单产物无环网络先汇总共用需求，再计算上游，避免按每条支路重复取整。
export function calculateChain(input) {
  const targets = rates(input.targets, 'targets');
  const recipes = input.recipes;
  if (!Array.isArray(recipes)) throw new Error('recipes 必须是数组');
  const byOutput = new Map();
  for (const recipe of recipes) {
    const outputs = Object.entries(model(recipe).outputs).filter(([, amount]) => amount > 0);
    if (outputs.length !== 1) throw new Error('chain 使用单产物配方；多产物请用 balance');
    const item = outputs[0][0];
    if (byOutput.has(item)) throw new Error(`物品 ${item} 存在多个来源，请先选定配方`);
    byOutput.set(item, recipe);
  }
  const visiting = new Set();
  const visited = new Set();
  const order = [];
  function visit(item) {
    if (visiting.has(item)) throw new Error(`存在循环 ${item}；请用 balance 核验联合配平结果`);
    if (visited.has(item)) return;
    visiting.add(item);
    const recipe = byOutput.get(item);
    if (recipe) {
      for (const [raw, amount] of Object.entries(model(recipe).inputs)) {
        if (amount > 0) visit(raw);
      }
    }
    visiting.delete(item);
    visited.add(item);
    order.push(item);
  }
  for (const [item, amount] of Object.entries(targets)) if (amount > 0) visit(item);
  const demands = scaled(targets, 1);
  const external = Object.create(null);
  const steps = [];
  for (const item of order.reverse()) {
    const recipe = byOutput.get(item);
    const demand = demands[item] ?? 0;
    if (!recipe) external[item] = demand;
    else {
      const result = calculateRecipe({ recipe, targetItem: item, targetPerSecond: demand });
      add(demands, result.target.inputs);
      steps.push({ item, ...result });
    }
  }
  return { units: 'items/s', steps, externalInputs: external };
}

// 对已选择的执行速率核算毛流量与净流量，支持回流、多产物和增产剂自耗。
export function calculateBalance(input) {
  const targets = rates(input.targets, 'targets');
  const imports = rates(input.imports ?? {}, 'imports');
  if (!Array.isArray(input.steps)) throw new Error('steps 必须是数组');
  const produced = Object.create(null);
  const consumed = Object.create(null);
  const steps = input.steps.map(step => {
    const result = atCycles(step.recipe, step.cyclesPerSecond);
    add(produced, result.target.outputs);
    add(consumed, result.target.inputs);
    return result;
  });
  const items = new Set([...Object.keys(targets), ...Object.keys(imports),
    ...Object.keys(produced), ...Object.keys(consumed)]);
  const balance = Object.create(null);
  for (const item of items) {
    const net = (produced[item] ?? 0) + (imports[item] ?? 0) - (consumed[item] ?? 0);
    const residual = net - (targets[item] ?? 0);
    balance[item] = {
      produced: produced[item] ?? 0, consumed: consumed[item] ?? 0,
      imported: imports[item] ?? 0, target: targets[item] ?? 0, net,
      shortage: Math.max(0, -residual), surplus: Math.max(0, residual),
    };
  }
  return { units: 'items/s', steps, balance };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [, , command, file] = process.argv;
    const run = { recipe: calculateRecipe, chain: calculateChain, balance: calculateBalance }[command];
    if (!run || !file) throw new Error('用法：node production-calc.mjs recipe|chain|balance input.json（- 为标准输入）');
    const result = run(JSON.parse(readFileSync(file === '-' ? 0 : file, 'utf8')));
    console.log(JSON.stringify(result, null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
