using System;
using System.Reflection;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal sealed class AutomationClickBuildTool : BuildTool_Click
    {
        public void PrepareInventorySnapshot()
        {
            if (tmpPackage == null)
            {
                tmpPackage = new StorageComponent(player.package.size);
            }

            if (tmpPackage.size != player.package.size)
            {
                tmpPackage.SetSize(player.package.size);
            }

            Array.Copy(player.package.grids, tmpPackage.grids, tmpPackage.size);
            tmpInhandId = player.inhandItemId;
            tmpInhandCount = player.inhandItemCount;
        }

        public void SetCastVein(int veinId, Vector3 veinPosition)
        {
            SetFieldIfCompatible("castObjectId", veinId);
            SetFieldIfCompatible("castVeinId", veinId);
            SetFieldIfCompatible("currentCastVeinId", veinId);
            SetFieldIfCompatible("castObjectPos", veinPosition);
        }

        private void SetFieldIfCompatible(string name, object value)
        {
            for (var type = GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    continue;
                }

                if (value == null || field.FieldType.IsInstanceOfType(value))
                {
                    field.SetValue(this, value);
                }
                else if (field.FieldType == typeof(int) && value is int)
                {
                    field.SetValue(this, value);
                }

                return;
            }
        }
    }

    internal sealed class AutomationPathBuildTool : BuildTool_Path
    {
        public void PrepareInventorySnapshot()
        {
            if (tmpPackage == null)
            {
                tmpPackage = new StorageComponent(player.package.size);
            }

            if (tmpPackage.size != player.package.size)
            {
                tmpPackage.SetSize(player.package.size);
            }

            Array.Copy(player.package.grids, tmpPackage.grids, tmpPackage.size);
            tmpInhandId = player.inhandItemId;
            tmpInhandCount = player.inhandItemCount;
        }
    }

    internal sealed class AutomationInserterBuildTool : BuildTool_Inserter
    {
        public void PrepareInventorySnapshot()
        {
            if (tmpPackage == null)
            {
                tmpPackage = new StorageComponent(player.package.size);
            }

            if (tmpPackage.size != player.package.size)
            {
                tmpPackage.SetSize(player.package.size);
            }

            Array.Copy(player.package.grids, tmpPackage.grids, tmpPackage.size);
            tmpInhandId = player.inhandItemId;
            tmpInhandCount = player.inhandItemCount;
        }
    }
}
