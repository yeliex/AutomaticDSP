using System.Collections.Generic;
using UnityEngine;

namespace AutomaticDSP.State
{
    internal sealed class StateQueryField
    {
        public StateQueryField(string name, string responseName)
        {
            Name = name;
            ResponseName = responseName;
            Children = new List<StateQueryField>();
            Filters = new List<StateQueryFilter>();
        }

        public string Name { get; }

        public string ResponseName { get; }

        public int? Limit { get; set; }

        public int? Offset { get; set; }

        public int? EntityId { get; set; }

        public Vector3? Position { get; set; }

        public List<StateQueryFilter> Filters { get; }

        public List<StateQueryField> Children { get; }

        public StateQueryField Clone()
        {
            var clone = new StateQueryField(Name, ResponseName)
            {
                Limit = Limit,
                Offset = Offset,
                EntityId = EntityId,
                Position = Position
            };

            foreach (var child in Children)
            {
                clone.Children.Add(child.Clone());
            }

            foreach (var filter in Filters)
            {
                clone.Filters.Add(filter.Clone());
            }

            return clone;
        }

        public void Merge(StateQueryField other)
        {
            if (!System.Nullable.Equals(Position, other.Position))
            {
                throw new StateQueryParseException("同一响应字段不能指定不同的位置；请使用别名。");
            }
            if (EntityId != other.EntityId)
            {
                throw new StateQueryParseException("同一响应字段不能指定不同的 entityId；请使用别名。");
            }
            if (!Limit.HasValue && other.Limit.HasValue)
            {
                Limit = other.Limit;
            }

            if (!Offset.HasValue && other.Offset.HasValue)
            {
                Offset = other.Offset;
            }

            foreach (var filter in other.Filters)
            {
                Filters.Add(filter.Clone());
            }

            foreach (var otherChild in other.Children)
            {
                var child = FindChild(otherChild.ResponseName, otherChild.Name);
                if (child == null)
                {
                    Children.Add(otherChild.Clone());
                }
                else
                {
                    child.Merge(otherChild);
                }
            }
        }

        private StateQueryField FindChild(string responseName, string name)
        {
            foreach (var child in Children)
            {
                if (child.ResponseName == responseName && child.Name == name)
                {
                    return child;
                }
            }

            return null;
        }
    }
}
