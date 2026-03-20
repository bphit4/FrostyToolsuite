using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Frosty.Sdk;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Interfaces;
using FrostyEditor.Models;

namespace FrostyEditor.Managers;

public sealed record InspectorClipboardEntry(string NodeName, Type DataType, object Data, bool IsObject);

public sealed partial class InspectorClipboard : ObservableObject
{
    public static InspectorClipboard Current { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private InspectorClipboardEntry? m_entry;

    public bool HasData => Entry is not null;

    public void Clear()
    {
        Entry = null;
    }

    public void SetData(string nodeName, object data, bool isObject)
    {
        Dictionary<object, object> oldNewMapping = new(ReferenceEqualityComparer.Instance);
        object copy = DeepCopyValue(data, null, ref oldNewMapping);
        Entry = new InspectorClipboardEntry(nodeName, data.GetType(), copy, isObject);
    }

    public bool TryCreatePasteValue(InspectorNodeModel node, EbxPartition? partition, out object? value, out string? error)
    {
        value = null;
        error = null;

        if (Entry is null)
        {
            error = "Clipboard is empty.";
            return false;
        }

        if (!node.SupportsPaste(Entry))
        {
            error = "The clipboard data does not match this property.";
            return false;
        }

        Dictionary<object, object> oldNewMapping = new(ReferenceEqualityComparer.Instance);
        value = DeepCopyValue(Entry.Data, partition, ref oldNewMapping);
        return true;
    }

    private static object DeepCopyValue(object obj, EbxPartition? partition, ref Dictionary<object, object> oldNewMapping)
    {
        Type objType = obj.GetType();
        if (objType.IsPrimitive || objType.IsEnum || obj is string || obj is decimal || obj is Guid || obj is DateTime || obj is DateTimeOffset || obj is TimeSpan)
        {
            return obj;
        }

        if (obj is PointerRef pointerRef)
        {
            return pointerRef.Type switch
            {
                PointerRefType.External => new PointerRef(pointerRef.External),
                PointerRefType.Internal when pointerRef.Internal is not null => new PointerRef((IEbxInstance)DeepCopy(pointerRef.Internal, partition, ref oldNewMapping)),
                _ => new PointerRef()
            };
        }

        if (obj is IList list)
        {
            IList newList = (IList)(Activator.CreateInstance(objType) ?? throw new InvalidOperationException($"Unable to create {objType.Name}."));
            foreach (object? item in list)
            {
                if (item is not null)
                {
                    newList.Add(DeepCopyValue(item, partition, ref oldNewMapping));
                }
            }

            return newList;
        }

        return DeepCopy(obj, partition, ref oldNewMapping);
    }

    private static object DeepCopy(object data, EbxPartition? partition, ref Dictionary<object, object> oldNewMapping)
    {
        Type dataType = data.GetType();
        if (dataType.IsValueType)
        {
            return data;
        }

        if (oldNewMapping.TryGetValue(data, out object? existing))
        {
            return existing;
        }

        object newData = TypeLibrary.CreateObject(dataType.Name) ?? Activator.CreateInstance(dataType) ?? throw new InvalidOperationException($"Unable to create {dataType.Name}.");
        oldNewMapping.Add(data, newData);

        if (data is IEbxInstance && newData is IEbxInstance newInstance)
        {
            AssetClassGuid guid = new AssetClassGuid(Guid.NewGuid(), -1);
            newInstance.SetInstanceGuid(guid);
            partition?.AddObject(newInstance);
        }

        foreach (PropertyInfo property in dataType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 || property.Name.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }

            object? oldValue = property.GetValue(data);
            if (oldValue is null)
            {
                property.SetValue(newData, null);
                continue;
            }

            property.SetValue(newData, DeepCopyValue(oldValue, partition, ref oldNewMapping));
        }

        return newData;
    }
}
