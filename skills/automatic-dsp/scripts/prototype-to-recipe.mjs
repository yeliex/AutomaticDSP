import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

// 只转换调用者明确选定的配方与设备，不选择配方、设备或增产模式。
export function fromPrototypes(snapshot, recipeId, machineItemId, targetItem, targetPerSecond) {
  const data = snapshot.data ?? snapshot;
  const recipe = data.recipes?.find(value => value.id === recipeId);
  const machine = data.items?.find(value => value.id === machineItemId);
  if (!recipe || !machine) throw new Error('快照中缺少指定配方或设备，请补齐对应目录页');
  if (recipe.unlocked !== true || machine.unlocked !== true) throw new Error('配方或设备尚未确认解锁');
  if (!['Smelt', 'Chemical', 'Refine', 'Assemble', 'Particle', 'Research'].includes(recipe.type)) {
    throw new Error('此加工类型需要专用计算，不能使用普通制造周期模型');
  }
  const desc = machine.prefabDesc;
  const assembler = desc?.isAssembler && desc.assemblerRecipeType === recipe.type;
  const lab = desc?.isLab && recipe.type === 'Research';
  if (!assembler && !lab) throw new Error('设备不支持指定配方类型，或缺少适配字段');
  const speed = assembler ? desc.assemblerSpeedMultiplier : desc.labSpeedMultiplier;
  for (const [name, value] of Object.entries({ timeSeconds: recipe.timeSeconds, machineSpeed: speed, targetPerSecond })) {
    if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) throw new Error(`${name} 必须是正有限数值`);
  }
  if (typeof desc.workPowerW !== 'number' || !Number.isFinite(desc.workPowerW) || desc.workPowerW < 0) {
    throw new Error('缺少有效的设备基础工作功率');
  }
  function amounts(entries) {
    if (!Array.isArray(entries) || entries.length === 0) throw new Error('缺少配方物料数组');
    const result = Object.create(null);
    for (const entry of entries) {
      if (!Number.isInteger(entry.itemId) || entry.itemId <= 0 ||
          !Number.isFinite(entry.count) || entry.count <= 0) throw new Error('配方物料 ID 或数量无效');
      result[entry.itemId] = (result[entry.itemId] ?? 0) + entry.count;
    }
    return result;
  }
  const inputs = amounts(recipe.items);
  const outputs = amounts(recipe.results);
  if (!Object.hasOwn(outputs, targetItem)) throw new Error('目标物品不是配方产物');
  return {
    targetItem: String(targetItem), targetPerSecond,
    recipe: { timeSeconds: recipe.timeSeconds, machineSpeed: speed,
      activePowerMW: desc.workPowerW / 1e6, inputs, outputs }
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [path, recipeId, machineId, targetId, rate] = process.argv.slice(2);
    if (!path || rate === undefined) throw new Error('用法：node prototype-to-recipe.mjs 快照.json 配方ID 设备物品ID 目标物品ID 每秒目标量');
    console.log(JSON.stringify(fromPrototypes(JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, '')),
      Number(recipeId), Number(machineId), targetId, Number(rate)), null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
