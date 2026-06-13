using System.Collections.Generic;

namespace AutomaticDSP.State
{
    internal enum StateQueryFilterOperator
    {
        Equals,
        NotEquals,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Contains,
        StartsWith,
        EndsWith,
        In
    }

    internal sealed class StateQueryFilter
    {
        public StateQueryFilter(string[] path, StateQueryFilterOperator operation, List<object> values)
        {
            Path = path;
            Operation = operation;
            Values = values;
        }

        public string[] Path { get; }

        public StateQueryFilterOperator Operation { get; }

        public List<object> Values { get; }

        public StateQueryFilter Clone()
        {
            return new StateQueryFilter((string[])Path.Clone(), Operation, new List<object>(Values));
        }
    }
}
