using System;
using System.Reflection;

namespace Shadowbus
{
    /// <summary>
    /// 反射扩展方法
    /// </summary>
    public static class ReflectionExtensions
    {
        /// <summary>
        /// 设置对象的字段值（包括私有字段）
        /// </summary>
        public static void SetField(this object obj, string fieldName, object value)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Type type = obj.GetType();
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field == null)
            {
                throw new ArgumentException($"Field '{fieldName}' not found in type '{type.Name}'");
            }

            field.SetValue(obj, value);
        }

        /// <summary>
        /// 获取对象的字段值（包括私有字段）
        /// </summary>
        public static object GetField(this object obj, string fieldName)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Type type = obj.GetType();
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field == null)
            {
                throw new ArgumentException($"Field '{fieldName}' not found in type '{type.Name}'");
            }

            return field.GetValue(obj);
        }

        /// <summary>
        /// 设置对象的属性值（包括私有属性）
        /// </summary>
        public static void SetProperty(this object obj, string propertyName, object value)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property == null)
            {
                throw new ArgumentException($"Property '{propertyName}' not found in type '{type.Name}'");
            }

            property.SetValue(obj, value);
        }

        /// <summary>
        /// 获取对象的属性值（包括私有属性）
        /// </summary>
        public static object GetProperty(this object obj, string propertyName)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property == null)
            {
                throw new ArgumentException($"Property '{propertyName}' not found in type '{type.Name}'");
            }

            return property.GetValue(obj);
        }
    }
}
