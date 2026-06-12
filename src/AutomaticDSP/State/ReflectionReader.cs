using System;
using System.Reflection;

namespace AutomaticDSP.State
{
    internal static class ReflectionReader
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;

        public static object Get(object target, params string[] names)
        {
            if (target == null)
            {
                return null;
            }

            var type = target.GetType();
            foreach (var name in names)
            {
                var field = type.GetField(name, Flags);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                var property = type.GetProperty(name, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }
            }

            return null;
        }

        public static int GetInt(object target, int defaultValue, params string[] names)
        {
            var value = Get(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        public static long GetLong(object target, long defaultValue, params string[] names)
        {
            var value = Get(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt64(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        public static bool GetBool(object target, bool defaultValue, params string[] names)
        {
            var value = Get(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        public static double GetDouble(object target, double defaultValue, params string[] names)
        {
            var value = Get(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
