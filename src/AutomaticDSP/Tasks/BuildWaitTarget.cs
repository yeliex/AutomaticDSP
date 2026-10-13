using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal sealed class BuildWaitTarget
    {
        public BuildWaitTarget(int objectId, int itemId, Vector3 position, BuildPreview preview)
        {
            ObjectId = objectId;
            ItemId = itemId;
            Position = position;
            if (preview != null)
            {
                Preview = new BuildPreview();
                Preview.Clone(preview);
            }
        }

        public int ObjectId { get; }

        public int ItemId { get; }

        public Vector3 Position { get; }

        public BuildPreview Preview { get; }

        public int EntityId { get; set; }

        public bool CreatedPrebuild => ObjectId < 0 && (Preview == null || Preview.coverObjId == 0);

        public bool PrebuildRemoved { get; set; }
    }
}
