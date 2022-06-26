// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Type = System.Type;
using System.Collections;

namespace Microsoft.AspNetCore.Grpc.JsonTranscoding.Internal.Json;

internal sealed class JsonConverterFactoryForMessage : JsonConverterFactory
{
    private readonly JsonContext _context;

    public JsonConverterFactoryForMessage(JsonContext context)
    {
        _context = context;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(IMessage).IsAssignableFrom(typeToConvert);
    }

    public override JsonConverter CreateConverter(
        Type typeToConvert, JsonSerializerOptions options)
    {
        JsonConverter converter = (JsonConverter)Activator.CreateInstance(
            typeof(MessageConverter<>).MakeGenericType(new Type[] { typeToConvert }),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: new object[] { _context },
            culture: null)!;

        return converter;
    }
}

internal sealed class MessageTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    private readonly JsonContext _context;

    public MessageTypeInfoResolver(JsonContext context)
    {
        _context = context;
    }

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);
        if (typeof(IMessage).IsAssignableFrom(type))
        {
            var messageDescriptor = JsonConverterHelper.GetMessageDescriptor(type);
            Debug.Assert(messageDescriptor != null);

            typeInfo.Properties.Clear();

            foreach (var field in messageDescriptor.Fields.InFieldNumberOrder())
            {
                //var accessor = field.Accessor;
                //var value = accessor.GetValue(message);
                //if (!ShouldFormatFieldValue(message, field, value, !settings.IgnoreDefaultValues))
                //{
                //    continue;
                //}

                //writer.WritePropertyName(accessor.Descriptor.JsonName);
                //JsonSerializer.Serialize(writer, value, value.GetType(), options);

                try
                {
                    var propertyInfo = typeInfo.CreateJsonPropertyInfo(
                        JsonConverterHelper.GetFieldType(field),
                        field.JsonName);

                    propertyInfo.ShouldSerialize = (o, oo) =>
                    {
                        ShouldFormatFieldValue((IMessage)o, field, oo, !_context.Settings.IgnoreDefaultValues);
                        return true;
                    };
                    propertyInfo.Get = (t) =>
                    {
                        return field.Accessor.GetValue((IMessage)t);
                    };

                    typeInfo.Properties.Add(propertyInfo);
                }
                catch (Exception ex)
                {
                    _ = ex;
                    throw;
                }
            }
        }
        return typeInfo;
    }

    private static Dictionary<string, FieldDescriptor> CreateJsonFieldMap(IList<FieldDescriptor> fields)
    {
        var map = new Dictionary<string, FieldDescriptor>();
        foreach (var field in fields)
        {
            map[field.Name] = field;
            map[field.JsonName] = field;
        }
        return new Dictionary<string, FieldDescriptor>(map);
    }

    /// <summary>
    /// Determines whether or not a field value should be serialized according to the field,
    /// its value in the message, and the settings of this formatter.
    /// </summary>
    private static bool ShouldFormatFieldValue(IMessage message, FieldDescriptor field, object? value, bool formatDefaultValues) =>
        field.HasPresence
        // Fields that support presence *just* use that
        ? field.Accessor.HasValue(message)
        // Otherwise, format if either we've been asked to format default values, or if it's
        // not a default value anyway.
        : formatDefaultValues || !IsDefaultValue(field, value);

    private static bool IsDefaultValue(FieldDescriptor descriptor, object? value)
    {
        if (descriptor.IsMap)
        {
            var dictionary = (IDictionary)value;
            return dictionary.Count == 0;
        }
        if (descriptor.IsRepeated)
        {
            var list = (IList)value;
            return list.Count == 0;
        }
        switch (descriptor.FieldType)
        {
            case FieldType.Bool:
                return (bool)value == false;
            case FieldType.Bytes:
                return (ByteString)value == ByteString.Empty;
            case FieldType.String:
                return (string)value == "";
            case FieldType.Double:
                return (double)value == 0.0;
            case FieldType.SInt32:
            case FieldType.Int32:
            case FieldType.SFixed32:
            case FieldType.Enum:
                return (int)value == 0;
            case FieldType.Fixed32:
            case FieldType.UInt32:
                return (uint)value == 0;
            case FieldType.Fixed64:
            case FieldType.UInt64:
                return (ulong)value == 0;
            case FieldType.SFixed64:
            case FieldType.Int64:
            case FieldType.SInt64:
                return (long)value == 0;
            case FieldType.Float:
                return (float)value == 0f;
            case FieldType.Message:
            case FieldType.Group: // Never expect to get this, but...
                return value == null;
            default:
                throw new ArgumentException("Invalid field type");
        }
    }
}
