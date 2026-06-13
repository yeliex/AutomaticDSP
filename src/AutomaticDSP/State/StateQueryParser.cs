using System;
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
            if (operation == null)
            {
                throw new StateQueryParseException("Request must include a GraphQL query operation.");
            }

            if (operation.Operation != OperationType.Query)
            {
                throw new StateQueryParseException("Only GraphQL query operations are supported by /game/state.");
            }

            var plan = new StateQueryPlan();
            AddSelections(plan.Fields, operation.SelectionSet, fragments, new HashSet<string>());
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

            foreach (var filter in ParseWhereFilters(field.Arguments))
            {
                queryField.Filters.Add(filter);
            }

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

        private static List<StateQueryFilter> ParseWhereFilters(GraphQLArguments arguments)
        {
            var filters = new List<StateQueryFilter>();
            if (arguments?.Items == null)
            {
                return filters;
            }

            foreach (var argument in arguments.Items)
            {
                if (argument.Name.StringValue != "where")
                {
                    continue;
                }

                if (!(argument.Value is GraphQLObjectValue whereValue))
                {
                    throw new StateQueryParseException("where argument must be an object.");
                }

                if (whereValue.Fields == null)
                {
                    return filters;
                }

                foreach (var filterField in whereValue.Fields)
                {
                    filters.Add(ParseFilter(filterField));
                }
            }

            return filters;
        }

        private static StateQueryFilter ParseFilter(GraphQLObjectField field)
        {
            var rawName = field.Name.StringValue;
            var operation = StateQueryFilterOperator.Equals;
            var pathName = rawName;

            if (TryRemoveSuffix(rawName, "_startsWith", out pathName))
            {
                operation = StateQueryFilterOperator.StartsWith;
            }
            else if (TryRemoveSuffix(rawName, "_endsWith", out pathName))
            {
                operation = StateQueryFilterOperator.EndsWith;
            }
            else if (TryRemoveSuffix(rawName, "_contains", out pathName))
            {
                operation = StateQueryFilterOperator.Contains;
            }
            else if (TryRemoveSuffix(rawName, "_gte", out pathName))
            {
                operation = StateQueryFilterOperator.GreaterThanOrEqual;
            }
            else if (TryRemoveSuffix(rawName, "_lte", out pathName))
            {
                operation = StateQueryFilterOperator.LessThanOrEqual;
            }
            else if (TryRemoveSuffix(rawName, "_gt", out pathName))
            {
                operation = StateQueryFilterOperator.GreaterThan;
            }
            else if (TryRemoveSuffix(rawName, "_lt", out pathName))
            {
                operation = StateQueryFilterOperator.LessThan;
            }
            else if (TryRemoveSuffix(rawName, "_ne", out pathName))
            {
                operation = StateQueryFilterOperator.NotEquals;
            }
            else if (TryRemoveSuffix(rawName, "_in", out pathName))
            {
                operation = StateQueryFilterOperator.In;
            }

            var path = pathName.Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 0)
            {
                throw new StateQueryParseException("where filter field path cannot be empty.");
            }

            return new StateQueryFilter(path, operation, ParseFilterValues(field.Value));
        }

        private static bool TryRemoveSuffix(string value, string suffix, out string withoutSuffix)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                withoutSuffix = value.Substring(0, value.Length - suffix.Length);
                return true;
            }

            withoutSuffix = value;
            return false;
        }

        private static List<object> ParseFilterValues(GraphQLValue value)
        {
            var values = new List<object>();
            if (value is GraphQLListValue listValue)
            {
                if (listValue.Values == null)
                {
                    return values;
                }

                foreach (var item in listValue.Values)
                {
                    values.Add(ParseFilterScalar(item));
                }

                return values;
            }

            values.Add(ParseFilterScalar(value));
            return values;
        }

        private static object ParseFilterScalar(GraphQLValue value)
        {
            if (value is GraphQLIntValue intValue)
            {
                if (long.TryParse((string)intValue.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    return longValue;
                }
            }
            else if (value is GraphQLFloatValue floatValue)
            {
                if (double.TryParse((string)floatValue.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    return doubleValue;
                }
            }
            else if (value is GraphQLStringValue stringValue)
            {
                return (string)stringValue.Value;
            }
            else if (value is GraphQLBooleanValue booleanValue)
            {
                return booleanValue.BoolValue;
            }
            else if (value is GraphQLEnumValue enumValue)
            {
                return enumValue.Name.StringValue;
            }
            else if (value is GraphQLNullValue)
            {
                return null;
            }

            throw new StateQueryParseException("where filter values must be scalar literals.");
        }
    }
}
