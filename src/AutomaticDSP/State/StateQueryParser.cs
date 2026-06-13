using System.Collections.Generic;
using System.Globalization;
using GraphQLParser;
using GraphQLParser.AST;

namespace AutomaticDSP.State
{
    internal static class StateQueryParser
    {
        public static StateQueryPlan Parse(string query, string operationName)
        {
            var document = Parser.Parse(query, new ParserOptions { Ignore = IgnoreOptions.All });
            var fragments = CollectFragments(document);
            var operation = FindOperation(document, operationName);
            var plan = new StateQueryPlan();
            AddSelections(plan.Fields, operation?.SelectionSet, fragments, new HashSet<string>());
            return plan;
        }

        private static Dictionary<string, GraphQLFragmentDefinition> CollectFragments(GraphQLDocument document)
        {
            var fragments = new Dictionary<string, GraphQLFragmentDefinition>();
            foreach (var definition in document.Definitions)
            {
                if (definition is GraphQLFragmentDefinition fragment)
                {
                    fragments[fragment.FragmentName.Name.StringValue] = fragment;
                }
            }

            return fragments;
        }

        private static GraphQLOperationDefinition FindOperation(GraphQLDocument document, string operationName)
        {
            GraphQLOperationDefinition fallback = null;
            foreach (var definition in document.Definitions)
            {
                if (!(definition is GraphQLOperationDefinition operation))
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = operation;
                }

                if (!string.IsNullOrWhiteSpace(operationName) &&
                    (object)operation.Name != null &&
                    operation.Name.StringValue == operationName)
                {
                    return operation;
                }
            }

            return fallback;
        }

        private static void AddSelections(
            List<StateQueryField> fields,
            GraphQLSelectionSet selectionSet,
            Dictionary<string, GraphQLFragmentDefinition> fragments,
            HashSet<string> fragmentStack)
        {
            if (selectionSet?.Selections == null)
            {
                return;
            }

            foreach (var selection in selectionSet.Selections)
            {
                if (selection is GraphQLField field)
                {
                    AddField(fields, field, fragments, fragmentStack);
                }
                else if (selection is GraphQLInlineFragment inlineFragment)
                {
                    AddSelections(fields, inlineFragment.SelectionSet, fragments, fragmentStack);
                }
                else if (selection is GraphQLFragmentSpread fragmentSpread)
                {
                    var name = fragmentSpread.FragmentName.Name.StringValue;
                    if (fragments.TryGetValue(name, out var fragment) && fragmentStack.Add(name))
                    {
                        AddSelections(fields, fragment.SelectionSet, fragments, fragmentStack);
                        fragmentStack.Remove(name);
                    }
                }
            }
        }

        private static void AddField(
            List<StateQueryField> fields,
            GraphQLField field,
            Dictionary<string, GraphQLFragmentDefinition> fragments,
            HashSet<string> fragmentStack)
        {
            var name = field.Name.StringValue;
            var responseName = field.Alias?.Name?.StringValue ?? name;
            var queryField = new StateQueryField(name, responseName)
            {
                Limit = IntArgument(field.Arguments, "limit"),
                Offset = IntArgument(field.Arguments, "offset")
            };
            AddSelections(queryField.Children, field.SelectionSet, fragments, fragmentStack);

            var existing = FindField(fields, responseName, name);
            if (existing == null)
            {
                fields.Add(queryField);
            }
            else
            {
                existing.Merge(queryField);
            }
        }

        private static StateQueryField FindField(List<StateQueryField> fields, string responseName, string name)
        {
            foreach (var field in fields)
            {
                if (field.ResponseName == responseName && field.Name == name)
                {
                    return field;
                }
            }

            return null;
        }

        private static int? IntArgument(GraphQLArguments arguments, string name)
        {
            if (arguments?.Items == null)
            {
                return null;
            }

            foreach (var argument in arguments.Items)
            {
                if (argument.Name.StringValue != name || !(argument.Value is GraphQLIntValue intValue))
                {
                    continue;
                }

                if (int.TryParse((string)intValue.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
