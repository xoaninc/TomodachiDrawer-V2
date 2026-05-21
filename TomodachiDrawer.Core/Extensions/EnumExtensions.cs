using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TomodachiDrawer.Core.Extensions
{
    // This is for the Description enum, specifically TomodachiLifeMask.
    // this is just some code from another project I had to cache it, realistically this is overkill since each fetch was only like... a microsecond.
    public static class EnumExtensions
    {
        private static readonly ConcurrentDictionary<Type, Dictionary<string, string>> _descriptionCache = new();

        public static string GetDescription(this Enum value)
        {
            var type = value.GetType();
            var map = _descriptionCache.GetOrAdd(type, BuildDescriptionMap);

            var key = value.ToString();
            if (map.TryGetValue(key, out var descVal))
                return descVal;
            return key;
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)]
        private static Dictionary<string, string> BuildDescriptionMap(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type t)
        {
            var dict = new Dictionary<string, string>();
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var name = field.Name;
                var attr = field.GetCustomAttribute<DescriptionAttribute>();
                var desc = attr?.Description ?? name;
                dict[name] = desc;
            }
            return dict;
        }
    }

}
