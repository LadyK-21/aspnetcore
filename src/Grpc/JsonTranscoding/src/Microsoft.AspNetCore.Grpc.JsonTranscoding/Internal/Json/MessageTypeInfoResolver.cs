// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Type = System.Type;
using System.Collections;
using Grpc.Shared;
using Google.Protobuf.WellKnownTypes;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.AspNetCore.Grpc.JsonTranscoding.Internal.Json;

internal sealed class MessageTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    private readonly JsonContext _context;

    public MessageTypeInfoResolver(JsonContext context)
    {
        _context = context;
    }

    private static bool IsStandardMessage(Type type, [NotNullWhen(true)] out MessageDescriptor? messageDescriptor)
    {
        if (!typeof(IMessage).IsAssignableFrom(type))
        {
            messageDescriptor = null;
            return false;
        }

        messageDescriptor = JsonConverterHelper.GetMessageDescriptor(type);
        if (messageDescriptor == null)
        {
            return false;
        }
        if (ServiceDescriptorHelpers.IsWrapperType(messageDescriptor))
        {
            return false;
        }
        if (WellKnownTypeNames.ContainsKey(messageDescriptor.FullName))
        {
            return false;
        }

        return true;
    }

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);
        if (IsStandardMessage(type, out var messageDescriptor))
        {
            typeInfo.Properties.Clear();

            var fields = messageDescriptor.Fields.InFieldNumberOrder();
            var mappings = CreateJsonFieldMap(fields);

            foreach (var field in fields)
            {
                mappings.Remove(field.JsonName);

                var propertyInfo = CreatePropertyInfo(typeInfo, field.JsonName, field, isWritable: true);
                typeInfo.Properties.Add(propertyInfo);
            }

            foreach (var mapping in mappings)
            {
                var propertyInfo = CreatePropertyInfo(typeInfo, mapping.Key, mapping.Value.FieldDescriptor, isWritable: false);
                typeInfo.Properties.Add(propertyInfo);
            }
        }
        return typeInfo;
    }

    private JsonPropertyInfo CreatePropertyInfo(JsonTypeInfo typeInfo, string name, FieldDescriptor field, bool isWritable)
    {
        var propertyInfo = typeInfo.CreateJsonPropertyInfo(
            JsonConverterHelper.GetFieldType(field),
            name);

        if (isWritable)
        {
            propertyInfo.ShouldSerialize = (o, oo) =>
            {
                return JsonConverterHelper.ShouldFormatFieldValue((IMessage)o, field, oo, !_context.Settings.IgnoreDefaultValues);
            };
            propertyInfo.Get = (t) =>
            {
                return field.Accessor.GetValue((IMessage)t);
            };
        }

        propertyInfo.Set = GetSetMethod(field);

        return propertyInfo;
    }

    private static Action<object, object?> GetSetMethod(FieldDescriptor field)
    {
        if (field.IsMap)
        {
            return (o, v) =>
            {

                var existingValue = (IDictionary)field.Accessor.GetValue((IMessage)o);
                foreach (DictionaryEntry item in (IDictionary)v!)
                {
                    existingValue[item.Key] = item.Value;
                }
            };
        }

        if (field.IsRepeated)
        {
            return (o, v) =>
            {
                var existingValue = (IList)field.Accessor.GetValue((IMessage)o);
                foreach (var item in (IList)v!)
                {
                    existingValue.Add(item);
                }
            };
        }

        if (field.RealContainingOneof != null)
        {
            return (o, v) =>
            {
                var caseField = field.RealContainingOneof.Accessor.GetCaseFieldDescriptor((IMessage)o);
                if (caseField != null)
                {
                    throw new InvalidOperationException($"Multiple values specified for oneof {field.RealContainingOneof.Name}.");
                }

                field.Accessor.SetValue((IMessage)o, v);
            };
        }

        return (o, v) =>
        {
            field.Accessor.SetValue((IMessage)o, v);
        };
    }

    private static Dictionary<string, MappedField> CreateJsonFieldMap(IList<FieldDescriptor> fields)
    {
        var map = new Dictionary<string, MappedField>();
        foreach (var field in fields)
        {
            map[field.Name] = new(IsWritable: false, field);
            map[field.JsonName] = new(IsWritable: true, field);
        }
        return new Dictionary<string, MappedField>(map);
    }

    private record struct MappedField(bool IsWritable, FieldDescriptor FieldDescriptor);

    private static readonly Dictionary<string, Type> WellKnownTypeNames = new Dictionary<string, Type>
    {
        [Any.Descriptor.FullName] = typeof(AnyConverter<>),
        [Duration.Descriptor.FullName] = typeof(DurationConverter<>),
        [Timestamp.Descriptor.FullName] = typeof(TimestampConverter<>),
        [FieldMask.Descriptor.FullName] = typeof(FieldMaskConverter<>),
        [Struct.Descriptor.FullName] = typeof(StructConverter<>),
        [ListValue.Descriptor.FullName] = typeof(ListValueConverter<>),
        [Value.Descriptor.FullName] = typeof(ValueConverter<>),
    };
}
