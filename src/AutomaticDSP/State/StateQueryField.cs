using System.Collections.Generic;

namespace AutomaticDSP.State
{
    internal sealed class StateQueryField
    {
        public StateQueryField(string name, string responseName)
        {
            Name = name;
            ResponseName = responseName;
            Children = new List<StateQueryField>();
        }

        public string Name { get; }

        public string ResponseName { get; }

        public int? Limit { get; set; }

        public int? Offset { get; set; }

        public List<StateQueryField> Children { get; }

        public StateQueryField Clone()
        {
            var clone = new StateQueryField(Name, ResponseName)
            {
                Limit = Limit,
                Offset = Offset
            };

            foreach (var child in Children)
            {
                clone.Children.Add(child.Clone());
            }

            return clone;
        }

        public void Merge(StateQueryField other)
        {
            if (!Limit.HasValue && other.Limit.HasValue)
            {
                Limit = other.Limit;
            }

            if (!Offset.HasValue && other.Offset.HasValue)
            {
                Offset = other.Offset;
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
