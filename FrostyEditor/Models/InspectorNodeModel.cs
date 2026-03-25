using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Frosty.Sdk;
using Frosty.Sdk.Attributes;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.IO.Ebx;
using Frosty.Sdk.Managers;
using FrostyEditor.Managers;

namespace FrostyEditor.Models;

public sealed partial class InspectorNodeModel : ObservableObject
{
    private static readonly Bitmap s_referenceIcon = LoadLegacyBitmap("Reference.png");
    private static readonly Bitmap s_classRefIcon = LoadLegacyBitmap("ClassRef.png");
    private static readonly Dictionary<string, int> s_layerCompPropertyOrder = new(StringComparer.Ordinal)
    {
        ["Guid"] = 0,
        ["Name"] = 1,
        ["CID_Mask_Layers"] = 2
    };
    private static readonly Dictionary<string, int> s_layerCompMapsPropertyOrder = new(StringComparer.Ordinal)
    {
        ["colorTexture"] = 0,
        ["rsmTexture"] = 1,
        ["normalTexture"] = 2,
        ["layoutTexture"] = 3,
        ["isMask"] = 4,
        ["hasAO"] = 5,
        ["assetResolveRuleType"] = 6
    };
    private static readonly Dictionary<string, int> s_layerCompOverlayPropertyOrder = new(StringComparer.Ordinal)
    {
        ["info"] = 0,
        ["textures"] = 1,
        ["rsm"] = 2,
        ["tint"] = 3,
        ["overlay_blend"] = 4,
        ["overlay_transform"] = 5
    };
    private static readonly Dictionary<string, int> s_layerCompMaterialPropertyOrder = new(StringComparer.Ordinal)
    {
        ["info"] = 0,
        ["textures"] = 1,
        ["rsm"] = 2,
        ["tint"] = 3,
        ["material_transform"] = 4,
        ["label"] = 5
    };
    private Func<IEnumerable<InspectorNodeModel>>? m_childFactory;
    private readonly Func<string, InspectorSetResult>? m_setValueFromText;
    private readonly Func<object?>? m_getCurrentValue;
    private readonly Action<InspectorNodeModel>? m_onValueCommitted;
    private readonly IInspectorValueAccessor? m_valueAccessor;
    private readonly Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? m_getOwningAssetEntry;
    private readonly Func<EbxPartition?>? m_getOwningPartition;
    private readonly bool m_expandInlinePointerChildren;
    private readonly bool m_hideCollectionChildren;
    private readonly Type? m_pointerBaseType;
    private bool m_childrenLoaded;
    private bool? m_cachedBooleanValue;
    private bool m_cachedIsPointerRef;
    private bool m_cachedIsResourceRef;
    private bool m_cachedIsCollectionNode;
    private bool m_cachedCanAddCollectionItem;
    private bool m_cachedCanClearCollectionItems;
    private bool m_cachedCanClearPointer;
    private bool m_cachedCanOpenReferenceAsset;
    private bool m_cachedCanCreatePointer;
    private string? m_cachedPointerGuidText = string.Empty;
    private PointerDisplayParts m_cachedPointerDisplayParts = new(string.Empty, string.Empty, string.Empty);
    private Bitmap m_cachedPointerTypeIcon = s_referenceIcon;
    private Type? m_cachedPointerTargetType;
    private Frosty.Sdk.Managers.Entries.EbxAssetEntry? m_cachedReferencedAssetEntry;
    private bool m_skipEnsureChildrenOnExpand;

    public string Name { get; }
    public string NodePath { get; }
    public string TypeName { get; }
    public int Depth { get; }
    public ObservableCollection<InspectorNodeModel> Children { get; } = [];
    public bool IsPlaceholder { get; }
    public bool AreChildrenLoaded => m_childrenLoaded;
    public bool HasChildren => m_childFactory is not null || Children.Any(child => !child.IsPlaceholder);
    public bool ShowExpanderSpacer => !HasChildren;
    public bool ShowExpandGlyph => HasChildren;
    public string ExpandGlyphData => IsExpanded ? "M0,0 L6,0 L3,6 Z" : "M0,0 L0,6 L6,3 Z";
    public Thickness NameIndent => new(4 + (Depth * 12), 0, 0, 0);
    public Thickness FlatNameIndent => new(Math.Max(0, Depth) * 12, 0, 0, 0);
    public bool IsEditable => m_setValueFromText is not null;
    public bool IsTextVisible => !IsEditing && !IsBoolean && !IsPointerRef;
    public bool IsPointerDisplayVisible => !IsEditing && !IsBoolean && IsPointerRef;
    public bool IsEditorVisible => IsEditable && IsEditing;
    public bool IsBoolean => m_cachedBooleanValue.HasValue;
    public bool IsBooleanVisible => IsBoolean && !IsEditing;
    public bool CanCopyValue => !string.IsNullOrWhiteSpace(Value);
    public bool CanCopyObject => !IsPlaceholder && m_getCurrentValue?.Invoke() is not null;
    public bool CanPasteObject => m_valueAccessor is not null && m_valueAccessor.CanWrite;
    public bool IsPointerRef => m_cachedIsPointerRef;
    public bool IsResourceRef => m_cachedIsResourceRef;
    public bool IsCollectionNode => m_cachedIsCollectionNode;
    public bool CanAddCollectionItem => m_cachedCanAddCollectionItem;
    public bool CanClearCollectionItems => m_cachedCanClearCollectionItems;
    public bool CanRemoveCollectionEntry => m_valueAccessor is ListItemValueAccessor accessor && accessor.CanRemove;
    public bool CanClearPointer => m_cachedCanClearPointer;
    public bool CanOpenReferenceAsset => m_cachedCanOpenReferenceAsset;
    public bool CanFindReferenceAsset => m_cachedCanOpenReferenceAsset;
    public bool CanCreatePointer => m_cachedCanCreatePointer;
    public bool CanAssignFromSelectedAsset => IsPointerRef && m_valueAccessor?.CanWrite == true && m_cachedPointerTargetType is not null;
    public bool ShowPointerOptions => IsPointerRef && (m_cachedCanClearPointer || m_cachedCanOpenReferenceAsset || m_cachedCanCreatePointer);
    public bool ShowRowActions => CanAddCollectionItem || CanClearCollectionItems || CanRemoveCollectionEntry || ShowPointerOptions;
    public string? PointerGuidText => m_cachedPointerGuidText;
    public string PointerDisplayName => m_cachedPointerDisplayParts.Name;
    public string PointerDisplayPath => m_cachedPointerDisplayParts.Path;
    public bool ShowPointerDisplayPath => !string.IsNullOrWhiteSpace(m_cachedPointerDisplayParts.Path);
    public string PointerTypeName => m_cachedPointerDisplayParts.TypeName;
    public Bitmap PointerTypeIcon => m_cachedPointerTypeIcon;
    public bool? BooleanValue
    {
        get => m_cachedBooleanValue;
        set
        {
            if (value.HasValue)
            {
                SetBooleanValue(value.Value);
            }
        }
    }
    public string NameSuffix => IsDirty ? "*" : string.Empty;
    public FontWeight NameFontWeight => (IsDirty || IsModified) ? FontWeight.SemiBold : FontWeight.Normal;

    [ObservableProperty]
    private string m_value;

    [ObservableProperty]
    private bool m_isExpanded;

    [ObservableProperty]
    private bool m_isEditing;

    [ObservableProperty]
    private string m_editorText = string.Empty;

    [ObservableProperty]
    private bool m_isDirty;

    [ObservableProperty]
    private bool m_isModified;

    internal InspectorNodeModel(
        string name,
        string value = "",
        string typeName = "",
        string nodePath = "",
        IEnumerable<InspectorNodeModel>? children = null,
        bool initiallyExpanded = false,
        Func<IEnumerable<InspectorNodeModel>>? childFactory = null,
        Func<object?>? getCurrentValue = null,
        Func<string, InspectorSetResult>? setValueFromText = null,
        Action<InspectorNodeModel>? onValueCommitted = null,
        IInspectorValueAccessor? valueAccessor = null,
        Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry = null,
        Func<EbxPartition?>? getOwningPartition = null,
        bool expandInlinePointerChildren = true,
        bool hideCollectionChildren = false,
        Type? pointerBaseType = null,
        bool isPlaceholder = false,
        int depth = 0)
    {
        Name = name;
        NodePath = nodePath;
        TypeName = typeName;
        Depth = depth;
        m_value = value;
        m_childFactory = childFactory;
        m_getCurrentValue = getCurrentValue;
        m_setValueFromText = setValueFromText;
        m_onValueCommitted = onValueCommitted;
        m_valueAccessor = valueAccessor;
        m_getOwningAssetEntry = getOwningAssetEntry;
        m_getOwningPartition = getOwningPartition;
        m_expandInlinePointerChildren = expandInlinePointerChildren;
        m_hideCollectionChildren = hideCollectionChildren;
        m_pointerBaseType = pointerBaseType;
        IsPlaceholder = isPlaceholder;
        m_isExpanded = initiallyExpanded;

        if (children is not null)
        {
            foreach (InspectorNodeModel child in children)
            {
                Children.Add(child.CloneForDepth(Depth + 1));
            }

            m_childrenLoaded = true;
        }
        else if (childFactory is not null)
        {
            Children.Add(CreatePlaceholder());
        }

        if (initiallyExpanded)
        {
            EnsureChildrenLoaded();
        }

        RefreshPresentationState();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !m_skipEnsureChildrenOnExpand)
        {
            EnsureChildrenLoaded();
        }

        OnPropertyChanged(nameof(ExpandGlyphData));
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTextVisible));
        OnPropertyChanged(nameof(IsPointerDisplayVisible));
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(IsBooleanVisible));
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(CanCopyValue));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(NameSuffix));
        OnPropertyChanged(nameof(NameFontWeight));
    }

    partial void OnIsModifiedChanged(bool value)
    {
        OnPropertyChanged(nameof(NameFontWeight));
    }

    public void EnsureChildrenLoaded()
    {
        if (m_childrenLoaded || m_childFactory is null)
        {
            return;
        }

        Children.Clear();
        foreach (InspectorNodeModel child in m_childFactory())
        {
            Children.Add(child);
        }

        m_childrenLoaded = true;
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowExpanderSpacer));
        OnPropertyChanged(nameof(ShowExpandGlyph));
    }

    public bool ExpandToDepth(int depth)
    {
        if (!HasChildren || depth < 0)
        {
            return false;
        }

        EnsureChildrenLoaded();
        bool changed = !IsExpanded;
        SetExpandedState(true, childrenAlreadyLoaded: true);

        if (depth == 0)
        {
            return changed;
        }

        foreach (InspectorNodeModel child in Children.Where(static child => !child.IsPlaceholder))
        {
            changed |= child.ExpandToDepth(depth - 1);
        }

        return changed;
    }

    public bool ExpandAllDescendants()
    {
        if (!HasChildren)
        {
            return false;
        }

        return SetBranchExpandedState(isExpanded: true);
    }

    public bool ExpandOneLevelProgressive()
    {
        if (!HasChildren)
        {
            return false;
        }

        int currentDepth = GetDeepestExpandedDepth();
        return ExpandToDepth(Math.Max(0, currentDepth + 1));
    }

    public bool CollapseAllDescendants()
    {
        if (!HasChildren)
        {
            return false;
        }

        return SetBranchExpandedState(isExpanded: false);
    }

    public bool CollapseOneLevelProgressive()
    {
        if (!HasChildren)
        {
            return false;
        }

        int currentDepth = GetDeepestExpandedDepth();
        if (currentDepth <= 0)
        {
            return SetBranchExpandedState(isExpanded: false);
        }

        return CollapseExpandedDepth(currentDepth);
    }

    public bool CollapseToDepth(int depth)
    {
        if (!HasChildren || depth < 0)
        {
            return false;
        }

        EnsureChildrenLoaded();
        bool changed = IsExpanded;
        SetExpandedState(false, childrenAlreadyLoaded: true);

        if (depth == 0)
        {
            return changed;
        }

        foreach (InspectorNodeModel child in Children.Where(static child => !child.IsPlaceholder))
        {
            changed |= child.CollapseToDepth(depth - 1);
        }

        return changed;
    }

    private bool ExpandEntireBranch()
    {
        if (!HasChildren)
        {
            return false;
        }

        EnsureChildrenLoaded();
        bool changed = !IsExpanded;
        SetExpandedState(true, childrenAlreadyLoaded: true);

        foreach (InspectorNodeModel child in Children.Where(static child => !child.IsPlaceholder))
        {
            changed |= child.ExpandEntireBranch();
        }

        return changed;
    }

    private bool CollapseEntireBranch()
    {
        if (!HasChildren)
        {
            return false;
        }

        EnsureChildrenLoaded();
        bool changed = IsExpanded;
        SetExpandedState(false, childrenAlreadyLoaded: true);

        foreach (InspectorNodeModel child in Children.Where(static child => !child.IsPlaceholder))
        {
            changed |= child.CollapseEntireBranch();
        }

        return changed;
    }

    private bool SetBranchExpandedState(bool isExpanded)
    {
        Stack<InspectorNodeModel> stack = new();
        stack.Push(this);
        bool changed = false;

        while (stack.Count > 0)
        {
            InspectorNodeModel node = stack.Pop();
            if (!node.HasChildren)
            {
                continue;
            }

            node.EnsureChildrenLoaded();
            changed |= node.IsExpanded != isExpanded;
            node.SetExpandedState(isExpanded, childrenAlreadyLoaded: true);

            foreach (InspectorNodeModel child in node.Children.Where(static child => !child.IsPlaceholder))
            {
                stack.Push(child);
            }
        }

        return changed;
    }

    private int GetDeepestExpandedDepth()
    {
        if (!HasChildren)
        {
            return -1;
        }

        EnsureChildrenLoaded();
        int deepestDepth = IsExpanded ? 0 : -1;
        Queue<(InspectorNodeModel Node, int Depth)> queue = new();
        queue.Enqueue((this, 0));

        while (queue.Count > 0)
        {
            (InspectorNodeModel node, int depth) = queue.Dequeue();
            if (!node.HasChildren || !node.IsExpanded)
            {
                continue;
            }

            deepestDepth = Math.Max(deepestDepth, depth);
            node.EnsureChildrenLoaded();
            foreach (InspectorNodeModel child in node.Children.Where(static child => !child.IsPlaceholder))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return deepestDepth;
    }

    private bool CollapseExpandedDepth(int targetDepth)
    {
        Queue<(InspectorNodeModel Node, int Depth)> queue = new();
        queue.Enqueue((this, 0));
        bool changed = false;

        while (queue.Count > 0)
        {
            (InspectorNodeModel node, int depth) = queue.Dequeue();
            if (!node.HasChildren)
            {
                continue;
            }

            node.EnsureChildrenLoaded();
            if (depth == targetDepth)
            {
                changed |= node.IsExpanded;
                node.SetExpandedState(false, childrenAlreadyLoaded: true);
                continue;
            }

            foreach (InspectorNodeModel child in node.Children.Where(static child => !child.IsPlaceholder))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return changed;
    }

    private void SetExpandedState(bool value, bool childrenAlreadyLoaded = false)
    {
        if (childrenAlreadyLoaded && value)
        {
            m_skipEnsureChildrenOnExpand = true;
        }

        try
        {
            IsExpanded = value;
        }
        finally
        {
            m_skipEnsureChildrenOnExpand = false;
        }
    }

    public bool BeginEdit()
    {
        if (!IsEditable || IsBoolean)
        {
            return false;
        }

        EditorText = GetFormattedCurrentValue();
        IsEditing = true;
        return true;
    }

    public bool CommitEdit(out string? error)
    {
        error = null;
        if (m_setValueFromText is null)
        {
            IsEditing = false;
            return false;
        }

        string currentValue = GetFormattedCurrentValue();
        if (string.Equals(EditorText, currentValue, StringComparison.Ordinal))
        {
            IsEditing = false;
            EditorText = currentValue;
            return true;
        }

        InspectorSetResult result = m_setValueFromText(EditorText);
        if (!result.Success)
        {
            error = result.ErrorMessage;
            return false;
        }

        Value = FormatLeafValue(NormalizeValue(result.Value));
        IsEditing = false;
        IsDirty = true;
        IsModified = true;
        RefreshPresentationState();
        m_onValueCommitted?.Invoke(this);
        return true;
    }

    public void CancelEdit()
    {
        EditorText = GetFormattedCurrentValue();
        IsEditing = false;
    }

    public string GetCopyValue()
    {
        return Value;
    }

    public object? GetCopyObject()
    {
        return m_getCurrentValue?.Invoke();
    }

    public bool SupportsPaste(InspectorClipboardEntry entry)
    {
        if (!CanPasteObject)
        {
            return false;
        }

        Type? targetType = NormalizeType(m_getCurrentValue?.Invoke()?.GetType() ?? m_valueAccessor?.ValueType);
        if (targetType is null || NormalizeType(entry.DataType) != targetType)
        {
            return false;
        }

        return !HasChildren || string.Equals(entry.NodeName, Name, StringComparison.OrdinalIgnoreCase);
    }

    public bool PasteObject(object? value, out string? error)
    {
        error = null;
        if (m_valueAccessor is null || !m_valueAccessor.CanWrite)
        {
            error = "This row is read only.";
            return false;
        }

        try
        {
            m_valueAccessor.SetValue(value);
            if (!HasChildren)
            {
                Value = GetFormattedCurrentValue();
            }

            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ToggleBoolean()
    {
        bool currentValue = BooleanValue ?? false;
        return SetBooleanValue(!currentValue);
    }

    public bool AddCollectionItem(out string? error)
    {
        error = null;
        if (!TryGetCollectionForEdit(out IList? list) || list is null || list.IsReadOnly)
        {
            error = "This collection is read only.";
            return false;
        }

        Type? elementType = GetCollectionElementType(list);
        if (elementType is null)
        {
            error = "Unable to determine the collection item type.";
            return false;
        }

        try
        {
            object? value = CoerceValueForType(CreateDefaultValue(elementType), elementType);
            if (value is IEbxInstance instance && m_getOwningPartition?.Invoke() is EbxPartition partition)
            {
                instance.SetInstanceGuid(new AssetClassGuid(Guid.NewGuid(), -1));
                partition.AddObject(instance);
            }

            MethodInfo? typedAdd = list.GetType().GetMethod("Add", [elementType]);
            if (typedAdd is not null)
            {
                typedAdd.Invoke(list, [value]);
            }
            else
            {
                list.Add(value);
            }

            Value = $"{list.Count:N0} item(s)";
            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ClearCollectionItems(out string? error)
    {
        error = null;
        if (!TryGetCollectionForEdit(out IList? list) || list is null || list.IsReadOnly || list.Count == 0)
        {
            error = "This collection has no items to clear.";
            return false;
        }

        try
        {
            for (int index = list.Count - 1; index >= 0; index--)
            {
                if (list[index] is IEbxInstance instance && m_getOwningPartition?.Invoke() is EbxPartition partition)
                {
                    partition.RemoveObject(instance);
                }

                list.RemoveAt(index);
            }

            Value = $"{list.Count:N0} item(s)";
            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool RemoveCollectionEntry(out string? error)
    {
        error = null;
        if (m_valueAccessor is not ListItemValueAccessor accessor || !accessor.CanRemove)
        {
            error = "This collection item cannot be removed.";
            return false;
        }

        try
        {
            accessor.RemoveItem(m_getOwningPartition?.Invoke());
            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ClearPointer(out string? error)
    {
        error = null;
        if (m_valueAccessor is null || !m_valueAccessor.CanWrite || GetCurrentRawValue() is not PointerRef)
        {
            error = "This pointer is read only.";
            return false;
        }

        try
        {
            m_valueAccessor.SetValue(new PointerRef());
            RefreshPointerPresentation();
            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool CreatePointerInstance(out string? error)
    {
        error = null;
        if (m_valueAccessor is null || !m_valueAccessor.CanWrite || GetCurrentRawValue() is not PointerRef)
        {
            error = "This pointer is read only.";
            return false;
        }

        Type? targetType = GetPointerTargetType();
        EbxPartition? partition = m_getOwningPartition?.Invoke();
        if (targetType is null || partition is null)
        {
            error = "Unable to determine the pointer type for creation.";
            return false;
        }

        try
        {
            object? value = CreateDefaultValue(targetType);
            if (value is not IEbxInstance instance)
            {
                error = $"Unable to create {targetType.Name}.";
                return false;
            }

            instance.SetInstanceGuid(new AssetClassGuid(Guid.NewGuid(), -1));
            partition.AddObject(instance);
            PointerRef pointer = new(instance);
            m_valueAccessor.SetValue(pointer);
            RefreshPointerPresentation();
            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryGetSelectedAssetPointerOptions(out IReadOnlyList<InspectorPointerAssignmentOption> options, out string? error)
    {
        options = Array.Empty<InspectorPointerAssignmentOption>();
        error = null;

        if (m_valueAccessor is null || !m_valueAccessor.CanWrite || GetCurrentRawValue() is not PointerRef)
        {
            error = "This pointer is read only.";
            return false;
        }

        Type? targetType = GetPointerTargetType();
        if (targetType is null)
        {
            error = "Unable to determine the pointer type for assignment.";
            return false;
        }

        Frosty.Sdk.Managers.Entries.EbxAssetEntry? selectedAsset = App.MainViewModel?.DataExplorer.SelectedEbxAssetEntry;
        if (selectedAsset is null)
        {
            error = "Select an EBX asset in Data Explorer first.";
            return false;
        }

        Frosty.Sdk.Managers.Entries.EbxAssetEntry? currentAsset = m_getOwningAssetEntry?.Invoke();
        if (currentAsset is not null && currentAsset.Guid == selectedAsset.Guid)
        {
            error = "Assign from selected asset expects a different EBX asset than the one currently being edited.";
            return false;
        }

        try
        {
            EbxPartition partition = AssetManager.GetEbxPartition(selectedAsset);
            List<InspectorPointerAssignmentOption> matches = [];
            foreach (IEbxInstance instance in partition.ExportedObjects)
            {
                Type instanceType = instance.GetType();
                if (!targetType.IsAssignableFrom(instanceType) && !TypeLibrary.IsSubClassOf(instanceType.Name, targetType.Name))
                {
                    continue;
                }

                AssetClassGuid guid = instance.GetInstanceGuid();
                matches.Add(new InspectorPointerAssignmentOption(
                    selectedAsset,
                    guid.ExportedGuid,
                    GetInstanceDisplayName(instance),
                    instanceType.Name,
                    guid));
            }

            if (matches.Count == 0)
            {
                error = $"No valid '{targetType.Name}' objects were found in '{selectedAsset.Filename}'.";
                return false;
            }

            options = matches
                .OrderBy(static option => !option.HasCustomTransientId)
                .ThenBy(static option => option.TypeName, StringComparer.Ordinal)
                .ThenBy(static option => option.InstanceGuid)
                .ToArray();

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool AssignPointerFromSelectedAsset(InspectorPointerAssignmentOption option, out string? error)
    {
        error = null;
        if (m_valueAccessor is null || !m_valueAccessor.CanWrite || GetCurrentRawValue() is not PointerRef)
        {
            error = "This pointer is read only.";
            return false;
        }

        try
        {
            m_valueAccessor.SetValue(new PointerRef(new EbxImportReference
            {
                PartitionGuid = option.AssetEntry.Guid,
                InstanceGuid = option.InstanceGuid
            }));

            RefreshPointerPresentation();
            MarkNodeChanged();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public InspectorNodeModel? CreateFilteredClone(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return CloneSubtree(IsExpanded);
        }

        filter = filter.Trim();
        EnsureChildrenLoaded();

        bool selfMatch = MatchesFilter(filter);
        List<InspectorNodeModel> filteredChildren = [];
        foreach (InspectorNodeModel child in Children.Where(static child => !child.IsPlaceholder))
        {
            InspectorNodeModel? filteredChild = child.CreateFilteredClone(filter);
            if (filteredChild is not null)
            {
                filteredChildren.Add(filteredChild);
            }
        }

        if (!selfMatch && filteredChildren.Count == 0)
        {
            return null;
        }

        return selfMatch
            // Preserve the current expansion state for direct matches so filtering
            // does not force-load or reopen entire nested GUID-heavy subtrees.
            ? CloneSubtree(initiallyExpanded: IsExpanded)
            : CloneWithChildren(filteredChildren, initiallyExpanded: true);
    }

    private string GetFormattedCurrentValue()
    {
        return FormatLeafValue(GetCurrentNormalizedValue() ?? Value);
    }

    private object? GetCurrentRawValue()
    {
        return m_valueAccessor?.GetRawValue() ?? m_getCurrentValue?.Invoke();
    }

    private object? GetCurrentNormalizedValue()
    {
        return m_valueAccessor?.GetNormalizedValue() ?? NormalizeValue(m_getCurrentValue?.Invoke());
    }

    public bool TryGetReferencedAssetEntry(out Frosty.Sdk.Managers.Entries.EbxAssetEntry? entry)
    {
        entry = null;
        if (GetCurrentRawValue() is not PointerRef pointer)
        {
            return false;
        }

        if (pointer.Type == PointerRefType.External)
        {
            entry = m_cachedReferencedAssetEntry;
            return entry is not null;
        }

        if (pointer.Type == PointerRefType.Internal)
        {
            entry = m_getOwningAssetEntry?.Invoke();
            return entry is not null;
        }

        return false;
    }

    private bool SetBooleanValue(bool value)
    {
        if (m_valueAccessor is null || !m_valueAccessor.CanWrite)
        {
            return false;
        }

        if (BooleanValue == value)
        {
            return true;
        }

        m_valueAccessor.SetValue(value);
        Value = value ? "True" : "False";
        MarkNodeChanged();
        return true;
    }

    private void MarkNodeChanged()
    {
        IsEditing = false;
        IsDirty = true;
        IsModified = true;
        RefreshPresentationState();
        RefreshChildSnapshotIfNeeded();
        OnPropertyChanged(nameof(BooleanValue));
        OnPropertyChanged(nameof(CanAddCollectionItem));
        OnPropertyChanged(nameof(CanClearCollectionItems));
        OnPropertyChanged(nameof(CanRemoveCollectionEntry));
        OnPropertyChanged(nameof(CanClearPointer));
        OnPropertyChanged(nameof(CanOpenReferenceAsset));
        OnPropertyChanged(nameof(CanFindReferenceAsset));
        OnPropertyChanged(nameof(CanCreatePointer));
        OnPropertyChanged(nameof(CanAssignFromSelectedAsset));
        OnPropertyChanged(nameof(ShowRowActions));
        OnPropertyChanged(nameof(PointerDisplayName));
        OnPropertyChanged(nameof(PointerDisplayPath));
        OnPropertyChanged(nameof(ShowPointerDisplayPath));
        OnPropertyChanged(nameof(PointerTypeName));
        OnPropertyChanged(nameof(PointerTypeIcon));
        OnPropertyChanged(nameof(PointerGuidText));
        OnPropertyChanged(nameof(IsTextVisible));
        OnPropertyChanged(nameof(IsPointerDisplayVisible));
        m_onValueCommitted?.Invoke(this);
    }

    private void RefreshChildSnapshotIfNeeded()
    {
        if (m_childFactory is null)
        {
            return;
        }

        m_childrenLoaded = false;
        Children.Clear();

        if (IsExpanded)
        {
            EnsureChildrenLoaded();
        }
        else
        {
            Children.Add(CreatePlaceholder());
        }

        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowExpanderSpacer));
        OnPropertyChanged(nameof(ShowExpandGlyph));
    }

    private bool TryGetCollectionForEdit(out IList? list)
    {
        list = GetCurrentRawValue() as IList;
        return list is not null && (m_valueAccessor?.CanWrite ?? true) && !m_hideCollectionChildren;
    }

    private static Type? GetCollectionElementType(IList list)
    {
        Type listType = list.GetType();
        if (listType.IsArray)
        {
            return null;
        }

        if (listType.IsGenericType)
        {
            Type[] arguments = listType.GetGenericArguments();
            if (arguments.Length == 1)
            {
                return arguments[0];
            }
        }

        return list.Count > 0 ? list[0]?.GetType() : null;
    }

    private Type? GetPointerTargetType()
    {
        return m_cachedPointerTargetType;
    }

    private static object? CreateDefaultValue(Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(IPrimitive).IsAssignableFrom(actualType))
        {
            return CreatePrimitiveValue(actualType, null);
        }

        if (actualType == typeof(string))
        {
            return string.Empty;
        }

        if (actualType.IsEnum)
        {
            Array values = Enum.GetValues(actualType);
            return values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(actualType);
        }

        if (actualType.IsValueType)
        {
            return Activator.CreateInstance(actualType);
        }

        return TypeLibrary.CreateObject(actualType.Name) ??
               Activator.CreateInstance(actualType) ??
               throw new InvalidOperationException($"Unable to construct {actualType.Name}.");
    }

    private static object? CoerceValueForType(object? value, Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(IPrimitive).IsAssignableFrom(actualType))
        {
            return CreatePrimitiveValue(actualType, value);
        }

        if (value is null)
        {
            return null;
        }

        if (actualType.IsInstanceOfType(value))
        {
            return value;
        }

        if (actualType.IsEnum)
        {
            return value is string text
                ? Enum.Parse(actualType, text, ignoreCase: true)
                : Enum.ToObject(actualType, value);
        }

        if (actualType == typeof(Guid))
        {
            return value is Guid guid ? guid : Guid.Parse(value.ToString() ?? string.Empty);
        }

        return Convert.ChangeType(value, actualType, CultureInfo.InvariantCulture);
    }

    private static object? CreatePrimitiveValue(Type primitiveType, object? value)
    {
        IPrimitive primitive = (IPrimitive)(Activator.CreateInstance(primitiveType)
                               ?? throw new InvalidOperationException($"Unable to construct {primitiveType.Name}."));
        SetPrimitiveValue(primitive, value);
        return primitive;
    }

    internal static void SetPrimitiveValue(IPrimitive primitive, object? value)
    {
        object? actualValue = primitive.ToActualType();
        Type actualType = actualValue?.GetType() ?? typeof(string);
        object? coercedValue = value;

        if (coercedValue is IPrimitive nestedPrimitive)
        {
            coercedValue = nestedPrimitive.ToActualType();
        }

        if (coercedValue is null)
        {
            coercedValue = CreateDefaultScalarValue(actualType);
        }
        else if (!actualType.IsInstanceOfType(coercedValue))
        {
            coercedValue = CoerceNonPrimitiveValue(coercedValue, actualType);
        }

        primitive.FromActualType(coercedValue ?? CreateDefaultScalarValue(actualType) ?? string.Empty);
    }

    private static object? CreateDefaultScalarValue(Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (actualType == typeof(string))
        {
            return string.Empty;
        }

        if (actualType.IsEnum)
        {
            Array values = Enum.GetValues(actualType);
            return values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(actualType);
        }

        return actualType.IsValueType ? Activator.CreateInstance(actualType) : null;
    }

    private static object? CoerceNonPrimitiveValue(object value, Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (actualType.IsInstanceOfType(value))
        {
            return value;
        }

        if (actualType.IsEnum)
        {
            return value is string text
                ? Enum.Parse(actualType, text, ignoreCase: true)
                : Enum.ToObject(actualType, value);
        }

        if (actualType == typeof(Guid))
        {
            return value is Guid guid ? guid : Guid.Parse(value.ToString() ?? string.Empty);
        }

        return Convert.ChangeType(value, actualType, CultureInfo.InvariantCulture);
    }

    private string GetPointerDisplayValue(PointerRef pointer)
    {
        return FormatPointerDisplay(pointer, m_getOwningAssetEntry);
    }

    private void RefreshPointerPresentation()
    {
        if (GetCurrentRawValue() is not PointerRef pointer)
        {
            return;
        }

        Value = GetPointerDisplayValue(pointer);
        SetChildFactoryForPointer(pointer);
        RefreshPresentationState();
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowExpanderSpacer));
        OnPropertyChanged(nameof(ShowExpandGlyph));
        OnPropertyChanged(nameof(IsTextVisible));
        OnPropertyChanged(nameof(IsPointerDisplayVisible));
        OnPropertyChanged(nameof(PointerDisplayName));
        OnPropertyChanged(nameof(PointerDisplayPath));
        OnPropertyChanged(nameof(ShowPointerDisplayPath));
        OnPropertyChanged(nameof(PointerTypeName));
        OnPropertyChanged(nameof(PointerTypeIcon));
    }

    private void SetChildFactoryForPointer(PointerRef pointer)
    {
        Func<IEnumerable<InspectorNodeModel>>? childFactory = null;
        if (m_expandInlinePointerChildren && pointer.Type == PointerRefType.Internal && pointer.Internal is not null)
        {
            object internalValue = pointer.Internal;
            childFactory = () => InspectorNodeBuilder.BuildInlineObjectChildren(
                internalValue,
                NodePath,
                Depth + 1,
                m_onValueCommitted,
                m_getOwningAssetEntry,
                m_getOwningPartition);
        }

        m_childFactory = childFactory;
        Children.Clear();

        if (m_childFactory is null)
        {
            m_childrenLoaded = true;
            OnPropertyChanged(nameof(ShowExpandGlyph));
            return;
        }

        m_childrenLoaded = false;
        if (IsExpanded)
        {
            EnsureChildrenLoaded();
        }
        else
        {
            Children.Add(CreatePlaceholder());
        }

        OnPropertyChanged(nameof(ShowExpandGlyph));
    }

    private bool MatchesFilter(string filter)
    {
        return Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               Value.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private InspectorNodeModel CloneSubtree(bool initiallyExpanded)
    {
        EnsureChildrenLoaded();
        List<InspectorNodeModel> children = [];
        if (Children.Any(static child => !child.IsPlaceholder))
        {
            children = Children
                .Where(static child => !child.IsPlaceholder)
                .Select(child => child.CloneSubtree(child.IsExpanded))
                .ToList();
        }

        return CloneWithChildren(children, initiallyExpanded);
    }

    private InspectorNodeModel CloneForDepth(int depth)
    {
        List<InspectorNodeModel>? children = null;
        if (Children.Any(static child => !child.IsPlaceholder))
        {
            children = Children
                .Where(static child => !child.IsPlaceholder)
                .Select(child => child.CloneForDepth(depth + 1))
                .ToList();
        }

        return new InspectorNodeModel(
            Name,
            Value,
            TypeName,
            NodePath,
            children,
            IsExpanded,
            children is null ? m_childFactory : null,
            m_getCurrentValue,
            m_setValueFromText,
            m_onValueCommitted,
            m_valueAccessor,
            m_getOwningAssetEntry,
            m_getOwningPartition,
            m_expandInlinePointerChildren,
            m_hideCollectionChildren,
            m_pointerBaseType,
            IsPlaceholder,
            depth)
        {
            IsDirty = IsDirty,
            IsModified = IsModified
        };
    }

    private InspectorNodeModel CloneWithChildren(IEnumerable<InspectorNodeModel>? children, bool initiallyExpanded)
    {
        return new InspectorNodeModel(
            Name,
            Value,
            TypeName,
            NodePath,
            children,
            initiallyExpanded,
            children is null ? m_childFactory : null,
            m_getCurrentValue,
            m_setValueFromText,
            m_onValueCommitted,
            m_valueAccessor,
            m_getOwningAssetEntry,
            m_getOwningPartition,
            m_expandInlinePointerChildren,
            m_hideCollectionChildren,
            m_pointerBaseType,
            IsPlaceholder,
            Depth)
        {
            IsDirty = IsDirty,
            IsModified = IsModified
        };
    }

    private static Type? NormalizeType(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(IPrimitive).IsAssignableFrom(actualType))
        {
            object? instance = Activator.CreateInstance(actualType);
            return NormalizeValue(instance)?.GetType() ?? actualType;
        }

        return actualType;
    }

    private static InspectorNodeModel CreatePlaceholder()
    {
        return new InspectorNodeModel(string.Empty, string.Empty, string.Empty, isPlaceholder: true);
    }

    private static string FormatLeafValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "True" : "False",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    internal static object? NormalizeValue(object? value)
    {
        if (value is IPrimitive primitive)
        {
            return NormalizeValue(primitive.ToActualType());
        }

        return value;
    }

    private string? TryGetPointerGuid()
    {
        return m_cachedPointerGuidText;
    }

    private PointerDisplayParts GetPointerDisplayParts()
    {
        return m_cachedPointerDisplayParts;
    }

    private string GetPointerTypeDisplayName()
    {
        return m_cachedPointerDisplayParts.TypeName;
    }

    private Bitmap GetPointerTypeIcon()
    {
        return m_cachedPointerTypeIcon;
    }

    private Type? ResolveCreatablePointerType()
    {
        Type? targetType = GetPointerTargetType();
        if (targetType is null)
        {
            return null;
        }

        return TypeLibrary.CreateObject(targetType.Name) is not null || !targetType.IsAbstract
            ? targetType
            : null;
    }

    private static bool TryResolveReferencedPointerObject(PointerRef pointer, out Frosty.Sdk.Managers.Entries.EbxAssetEntry? entry, out IEbxInstance? instance)
    {
        entry = null;
        instance = null;

        if (pointer.Type != PointerRefType.External)
        {
            return false;
        }

        entry = AssetManager.GetEbxAssetEntry(pointer.External.PartitionGuid) ??
                (pointer.External.InstanceGuid != Guid.Empty ? AssetManager.GetEbxAssetEntry(pointer.External.InstanceGuid) : null);
        if (entry is null)
        {
            return false;
        }

        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        if (pointer.External.InstanceGuid == Guid.Empty || partition.PrimaryInstanceGuid == pointer.External.InstanceGuid)
        {
            instance = partition.PrimaryInstance;
            return true;
        }

        instance = partition.GetObject(pointer.External.InstanceGuid);
        return instance is not null;
    }

    internal static string FormatPointerDisplay(PointerRef pointer, Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry)
    {
        PointerDisplayParts parts = GetPointerDisplayParts(pointer, getOwningAssetEntry);
        return string.IsNullOrWhiteSpace(parts.Path)
            ? parts.Name
            : $"{parts.Name} ({parts.Path})";
    }

    private static string GetInstanceDisplayName(object? instance)
    {
        if (instance is null)
        {
            return "(null)";
        }

        PropertyInfo? idProperty = instance.GetType().GetProperty("__Id", BindingFlags.Instance | BindingFlags.Public);
        object? idValue = idProperty?.GetValue(instance);
        if (idValue is not null)
        {
            string text = idValue.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return instance.GetType().GetCustomAttribute<DisplayNameAttribute>()?.Name ?? instance.GetType().Name;
    }

    private static PointerDisplayParts GetPointerDisplayParts(PointerRef pointer, Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry)
    {
        switch (pointer.Type)
        {
            case PointerRefType.Null:
                return new PointerDisplayParts("(null)", string.Empty, string.Empty);

            case PointerRefType.Internal:
                if (pointer.Internal is null)
                {
                    return new PointerDisplayParts("(null)", string.Empty, string.Empty);
                }

                return new PointerDisplayParts(
                    GetInstanceDisplayName(pointer.Internal),
                    getOwningAssetEntry?.Invoke()?.Name ?? "internal",
                    pointer.Internal.GetType().Name);

            case PointerRefType.External:
                if (!TryResolveReferencedPointerObject(pointer, out var entry, out IEbxInstance? instance) || entry is null)
                {
                    return new PointerDisplayParts("(invalid)", string.Empty, string.Empty);
                }

                EbxPartition partition = AssetManager.GetEbxPartition(entry);
                if (pointer.External.InstanceGuid == Guid.Empty || partition.PrimaryInstanceGuid == pointer.External.InstanceGuid)
                {
                    return new PointerDisplayParts(
                        entry.Filename,
                        entry.Name,
                        entry.Type ?? string.Empty);
                }

                return new PointerDisplayParts(
                    GetInstanceDisplayName(instance),
                    entry.Name,
                    instance?.GetType().Name ?? entry.Type ?? string.Empty);

            default:
                return new PointerDisplayParts(pointer.ToString() ?? string.Empty, string.Empty, string.Empty);
        }
    }

    private static Bitmap LoadLegacyBitmap(string fileName)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/{fileName}"));
        return new Bitmap(stream);
    }

    private void RefreshPresentationState()
    {
        object? rawValue = GetCurrentRawValue();
        object? normalizedValue = NormalizeValue(rawValue);

        m_cachedBooleanValue = normalizedValue is bool boolean ? boolean : null;
        m_cachedIsPointerRef = rawValue is PointerRef;
        m_cachedIsResourceRef = rawValue is ResourceRef;
        m_cachedIsCollectionNode = rawValue is IList;
        m_cachedCanAddCollectionItem = false;
        m_cachedCanClearCollectionItems = false;
        m_cachedCanClearPointer = false;
        m_cachedCanOpenReferenceAsset = false;
        m_cachedCanCreatePointer = false;
        m_cachedPointerGuidText = string.Empty;
        m_cachedPointerDisplayParts = new PointerDisplayParts(string.Empty, string.Empty, string.Empty);
        m_cachedPointerTypeIcon = s_referenceIcon;
        m_cachedPointerTargetType = null;
        m_cachedReferencedAssetEntry = null;

        if (rawValue is IList list && (m_valueAccessor?.CanWrite ?? true) && !m_hideCollectionChildren)
        {
            m_cachedCanAddCollectionItem = !list.IsReadOnly && GetCollectionElementType(list) is not null;
            m_cachedCanClearCollectionItems = !list.IsReadOnly && list.Count > 0;
        }

        if (rawValue is not PointerRef pointer)
        {
            return;
        }

        m_cachedCanClearPointer = m_valueAccessor?.CanWrite == true;
        m_cachedPointerGuidText = pointer.Type switch
        {
            PointerRefType.External => pointer.External.InstanceGuid != Guid.Empty
                ? pointer.External.InstanceGuid.ToString()
                : pointer.External.PartitionGuid.ToString(),
            PointerRefType.Internal when pointer.Internal is not null => pointer.Internal.GetInstanceGuid().ToString(),
            _ => string.Empty
        };

        switch (pointer.Type)
        {
            case PointerRefType.Null:
                m_cachedPointerDisplayParts = new PointerDisplayParts("(null)", string.Empty, string.Empty);
                m_cachedPointerTargetType = m_pointerBaseType;
                break;

            case PointerRefType.Internal:
                m_cachedPointerTypeIcon = s_classRefIcon;
                m_cachedPointerTargetType = pointer.Internal?.GetType() ?? m_pointerBaseType;
                m_cachedPointerDisplayParts = pointer.Internal is null
                    ? new PointerDisplayParts("(null)", string.Empty, string.Empty)
                    : new PointerDisplayParts(
                        GetInstanceDisplayName(pointer.Internal),
                        m_getOwningAssetEntry?.Invoke()?.Name ?? "internal",
                        pointer.Internal.GetType().Name);
                break;

            case PointerRefType.External:
                m_cachedReferencedAssetEntry = AssetManager.GetEbxAssetEntry(pointer.External.PartitionGuid);
                if (m_cachedReferencedAssetEntry is null && pointer.External.InstanceGuid != Guid.Empty)
                {
                    m_cachedReferencedAssetEntry = AssetManager.GetEbxAssetEntry(pointer.External.InstanceGuid);
                }

                m_cachedPointerTargetType = m_pointerBaseType;
                m_cachedCanOpenReferenceAsset = m_cachedReferencedAssetEntry is not null;
                if (m_cachedReferencedAssetEntry is not null)
                {
                    m_cachedPointerTypeIcon = AssetIconRegistry.GetIcon(m_cachedReferencedAssetEntry.Type);
                    m_cachedPointerDisplayParts = new PointerDisplayParts(
                        m_cachedReferencedAssetEntry.Filename,
                        m_cachedReferencedAssetEntry.Name,
                        m_cachedReferencedAssetEntry.Type ?? string.Empty);
                }
                else
                {
                    m_cachedPointerDisplayParts = new PointerDisplayParts("(invalid)", string.Empty, string.Empty);
                }
                break;

            default:
                m_cachedPointerTargetType = m_pointerBaseType;
                m_cachedPointerDisplayParts = new PointerDisplayParts(pointer.ToString() ?? string.Empty, string.Empty, string.Empty);
                break;
        }

        m_cachedCanCreatePointer = ResolveCreatablePointerType() is not null;
    }
}

public readonly record struct InspectorPointerAssignmentOption(
    Frosty.Sdk.Managers.Entries.EbxAssetEntry AssetEntry,
    Guid InstanceGuid,
    string DisplayName,
    string TypeName,
    AssetClassGuid AssetClassGuid)
{
    public bool HasCustomTransientId => !string.Equals(DisplayName, TypeName, StringComparison.Ordinal);
    public string MenuText => $"{DisplayName} [{TypeName}]";
}

internal readonly record struct PointerDisplayParts(string Name, string Path, string TypeName);

public readonly record struct InspectorSetResult(bool Success, object? Value, string? ErrorMessage)
{
    public static InspectorSetResult FromValue(object? value) => new(true, value, null);
    public static InspectorSetResult Failure(string message) => new(false, null, message);
}

internal interface IInspectorValueAccessor
{
    bool CanWrite { get; }
    Type ValueType { get; }
    object? GetRawValue();
    object? GetNormalizedValue();
    void SetValue(object? value);
}

internal sealed class PropertyValueAccessor : IInspectorValueAccessor
{
    private readonly object m_owner;
    private readonly PropertyInfo m_property;
    private readonly IInspectorValueAccessor? m_ownerAccessor;
    private readonly bool m_canWrite;

    public bool CanWrite => m_canWrite && m_property.CanWrite;
    public Type ValueType => m_property.PropertyType;

    public PropertyValueAccessor(object inOwner, PropertyInfo inProperty, IInspectorValueAccessor? inOwnerAccessor = null, bool canWrite = true)
    {
        m_owner = inOwner;
        m_property = inProperty;
        m_ownerAccessor = inOwnerAccessor;
        m_canWrite = canWrite;
    }

    public object? GetRawValue()
    {
        object target = m_ownerAccessor?.GetRawValue() ?? m_owner;
        return m_property.GetValue(target);
    }
    public object? GetNormalizedValue() => InspectorNodeModel.NormalizeValue(GetRawValue());

    public void SetValue(object? value)
    {
        object target = m_ownerAccessor?.GetRawValue() ?? m_owner;
        object? currentValue = m_property.GetValue(target);
        SetMemberValue(m_property.PropertyType, currentValue, value, v => m_property.SetValue(target, v));

        if (m_ownerAccessor is not null)
        {
            m_ownerAccessor.SetValue(target);
        }
    }

    private static void SetMemberValue(Type targetType, object? currentValue, object? newValue, Action<object?> assign)
    {
        Type actualTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (typeof(IPrimitive).IsAssignableFrom(actualTargetType))
        {
            IPrimitive primitive = currentValue as IPrimitive ??
                                   (IPrimitive)(Activator.CreateInstance(actualTargetType)
                                                ?? throw new InvalidOperationException($"Unable to construct {actualTargetType.Name}."));
            InspectorNodeModel.SetPrimitiveValue(primitive, newValue);
            assign(primitive);
            return;
        }

        assign(newValue);
    }
}

internal sealed class ListItemValueAccessor : IInspectorValueAccessor
{
    private readonly IList m_list;
    private readonly int m_index;
    private readonly bool m_canWrite;

    public bool CanWrite => m_canWrite && !m_list.IsReadOnly && m_index >= 0 && m_index < m_list.Count;
    public bool CanRemove => CanWrite;

    public ListItemValueAccessor(IList inList, int inIndex, bool canWrite = true)
    {
        m_list = inList;
        m_index = inIndex;
        m_canWrite = canWrite;
    }

    public Type ValueType
    {
        get
        {
            Type listType = m_list.GetType();
            if (listType.IsArray)
            {
                return listType.GetElementType() ?? typeof(object);
            }

            if (listType.IsGenericType)
            {
                Type[] arguments = listType.GetGenericArguments();
                if (arguments.Length == 1)
                {
                    return arguments[0];
                }
            }

            object? value = GetRawValue();
            return value?.GetType() ?? typeof(object);
        }
    }

    public object? GetRawValue() => CanWrite ? m_list[m_index] : null;
    public object? GetNormalizedValue() => InspectorNodeModel.NormalizeValue(GetRawValue());

    public void SetValue(object? value)
    {
        if (!CanWrite)
        {
            return;
        }

        Type actualTargetType = Nullable.GetUnderlyingType(ValueType) ?? ValueType;
        if (typeof(IPrimitive).IsAssignableFrom(actualTargetType))
        {
            IPrimitive primitive = GetRawValue() as IPrimitive ??
                                   (IPrimitive)(Activator.CreateInstance(actualTargetType)
                                                ?? throw new InvalidOperationException($"Unable to construct {actualTargetType.Name}."));
            InspectorNodeModel.SetPrimitiveValue(primitive, value);
            m_list[m_index] = primitive;
            return;
        }

        m_list[m_index] = value;
    }

    public void RemoveItem(EbxPartition? partition)
    {
        if (!CanRemove)
        {
            return;
        }

        if (m_list[m_index] is IEbxInstance instance && partition is not null)
        {
            partition.RemoveObject(instance);
        }

        m_list.RemoveAt(m_index);
    }
}

internal sealed class BoxedValueAccessor : IInspectorValueAccessor
{
    private readonly IInspectorValueAccessor m_ownerAccessor;

    public bool CanWrite => m_ownerAccessor.CanWrite;

    public Type ValueType
    {
        get
        {
            object? value = GetRawValue();
            return value?.GetType() ?? typeof(object);
        }
    }

    public BoxedValueAccessor(IInspectorValueAccessor inOwnerAccessor)
    {
        m_ownerAccessor = inOwnerAccessor;
    }

    public object? GetRawValue()
    {
        return m_ownerAccessor.GetRawValue() is BoxedValueRef boxedValueRef
            ? boxedValueRef.Value
            : null;
    }

    public object? GetNormalizedValue() => InspectorNodeModel.NormalizeValue(GetRawValue());

    public void SetValue(object? value)
    {
        if (!CanWrite || m_ownerAccessor.GetRawValue() is not BoxedValueRef boxedValueRef)
        {
            return;
        }

        boxedValueRef.SetValue(value!);
        m_ownerAccessor.SetValue(boxedValueRef);
    }
}

internal readonly record struct InspectorBuildOptions(
    bool ExpandInternalPointerChildren = true,
    bool HideChildren = false,
    Type? PointerBaseType = null);

public static class InspectorNodeBuilder
{
    private static readonly Dictionary<string, int> s_layerCompPropertyOrder = new(StringComparer.Ordinal)
    {
        ["Guid"] = 0,
        ["Name"] = 1,
        ["CID_Mask_Layers"] = 2
    };
    private static readonly Dictionary<string, int> s_layerCompMapsPropertyOrder = new(StringComparer.Ordinal)
    {
        ["colorTexture"] = 0,
        ["rsmTexture"] = 1,
        ["normalTexture"] = 2,
        ["layoutTexture"] = 3,
        ["isMask"] = 4,
        ["hasAO"] = 5,
        ["assetResolveRuleType"] = 6
    };
    private static readonly Dictionary<string, int> s_layerCompOverlayPropertyOrder = new(StringComparer.Ordinal)
    {
        ["info"] = 0,
        ["textures"] = 1,
        ["rsm"] = 2,
        ["tint"] = 3,
        ["overlay_blend"] = 4,
        ["overlay_transform"] = 5
    };
    private static readonly Dictionary<string, int> s_layerCompMaterialPropertyOrder = new(StringComparer.Ordinal)
    {
        ["info"] = 0,
        ["textures"] = 1,
        ["rsm"] = 2,
        ["tint"] = 3,
        ["material_transform"] = 4,
        ["label"] = 5
    };

    public static InspectorNodeModel BuildNode(string name, object? rawValue, Action<InspectorNodeModel>? onValueCommitted = null, bool initiallyExpanded = false, Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry = null, Func<EbxPartition?>? getOwningPartition = null)
    {
        return BuildNode(name, rawValue, name, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), onValueCommitted, null, initiallyExpanded, getOwningAssetEntry, getOwningPartition);
    }

    public static object? NormalizeValue(object? value)
    {
        return InspectorNodeModel.NormalizeValue(value);
    }

    internal static IEnumerable<InspectorNodeModel> BuildInlineObjectChildren(
        object value,
        string parentPath,
        int depth,
        Action<InspectorNodeModel>? onValueCommitted,
        Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry,
        Func<EbxPartition?>? getOwningPartition)
    {
        return BuildObjectChildren(
            value,
            null,
            parentPath,
            depth,
            new HashSet<object>(ReferenceEqualityComparer.Instance),
            onValueCommitted,
            getOwningAssetEntry,
            getOwningPartition);
    }

    private static InspectorNodeModel BuildNode(
        string name,
        object? rawValue,
        string nodePath,
        int depth,
        HashSet<object> visited,
        Action<InspectorNodeModel>? onValueCommitted,
        IInspectorValueAccessor? accessor,
        bool initiallyExpanded = false,
        Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry = null,
        Func<EbxPartition?>? getOwningPartition = null,
        InspectorBuildOptions options = default)
    {
        object? value = InspectorNodeModel.NormalizeValue(rawValue);
        if (value is null)
        {
            return new InspectorNodeModel(name, "null", "null", nodePath, initiallyExpanded: initiallyExpanded);
        }

        if (value is BoxedValueRef boxedValueRef)
        {
            object? boxedValue = boxedValueRef.Value;
            if (boxedValue is null)
            {
                return new InspectorNodeModel(
                    name,
                    "null",
                    nameof(BoxedValueRef),
                    nodePath,
                    initiallyExpanded: initiallyExpanded,
                    getCurrentValue: accessor is null ? (() => rawValue) : accessor.GetRawValue,
                    onValueCommitted: onValueCommitted,
                    valueAccessor: accessor,
                    getOwningAssetEntry: getOwningAssetEntry,
                    getOwningPartition: getOwningPartition,
                    expandInlinePointerChildren: options.ExpandInternalPointerChildren,
                    hideCollectionChildren: options.HideChildren,
                    pointerBaseType: options.PointerBaseType,
                    depth: depth);
            }

            IInspectorValueAccessor? boxedAccessor = accessor is null ? null : new BoxedValueAccessor(accessor);
            return BuildNode(
                name,
                boxedValue,
                nodePath,
                depth,
                visited,
                onValueCommitted,
                boxedAccessor,
                initiallyExpanded,
                getOwningAssetEntry,
                getOwningPartition,
                options);
        }

        Type type = value.GetType();
        Func<object?>? getCurrentValue = accessor is null ? (() => rawValue) : accessor.GetRawValue;
        Func<string, InspectorSetResult>? setValueFromText = CreateTextSetter(accessor);

        if (TryBuildSpecialLeaf(name, value, nodePath, out InspectorNodeModel? specialNode))
        {
            return specialNode!;
        }

        if (IsLeafValue(type, value))
        {
            return new InspectorNodeModel(
                name,
                FormatLeafValue(value),
                type.Name,
                nodePath,
                initiallyExpanded: initiallyExpanded,
                getCurrentValue: getCurrentValue,
                setValueFromText: setValueFromText,
                onValueCommitted: onValueCommitted,
                valueAccessor: accessor,
                getOwningAssetEntry: getOwningAssetEntry,
                getOwningPartition: getOwningPartition,
                expandInlinePointerChildren: options.ExpandInternalPointerChildren,
                hideCollectionChildren: options.HideChildren,
                pointerBaseType: options.PointerBaseType,
                depth: depth);
        }

        if (value is PointerRef pointer)
        {
            Func<IEnumerable<InspectorNodeModel>>? childFactory = null;
            if (options.ExpandInternalPointerChildren && pointer.Type == PointerRefType.Internal && pointer.Internal is not null)
            {
                object internalValue = pointer.Internal;
                HashSet<object> pointerChildVisited = CloneVisited(visited);
                childFactory = () => BuildObjectChildren(internalValue, null, nodePath, depth + 1, pointerChildVisited, onValueCommitted, getOwningAssetEntry, getOwningPartition);
            }

            return new InspectorNodeModel(
                name,
                FormatPointer(pointer, getOwningAssetEntry),
                nameof(PointerRef),
                nodePath,
                initiallyExpanded: initiallyExpanded,
                childFactory: childFactory,
                getCurrentValue: getCurrentValue,
                onValueCommitted: onValueCommitted,
                valueAccessor: accessor,
                getOwningAssetEntry: getOwningAssetEntry,
                getOwningPartition: getOwningPartition,
                expandInlinePointerChildren: options.ExpandInternalPointerChildren,
                hideCollectionChildren: options.HideChildren,
                pointerBaseType: options.PointerBaseType,
                depth: depth);
        }

        if (value is byte[] bytes)
        {
            return new InspectorNodeModel(name, $"{bytes.Length:N0} bytes", "byte[]", nodePath, initiallyExpanded: initiallyExpanded);
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            int itemCount = GetEnumerableCount(enumerable);
            HashSet<object> nextVisited = CloneVisited(visited);
            return new InspectorNodeModel(
                name,
                $"{itemCount:N0} item(s)",
                type.Name,
                nodePath,
                initiallyExpanded: initiallyExpanded,
                childFactory: options.HideChildren ? null : () => BuildEnumerableChildren(enumerable, accessor, nodePath, depth + 1, nextVisited, onValueCommitted, getOwningAssetEntry, getOwningPartition, options),
                getCurrentValue: getCurrentValue,
                setValueFromText: setValueFromText,
                onValueCommitted: onValueCommitted,
                valueAccessor: accessor,
                getOwningAssetEntry: getOwningAssetEntry,
                getOwningPartition: getOwningPartition,
                expandInlinePointerChildren: options.ExpandInternalPointerChildren,
                hideCollectionChildren: options.HideChildren,
                pointerBaseType: options.PointerBaseType,
                depth: depth);
        }

        if (!type.IsValueType)
        {
            if (!visited.Add(value))
            {
                return new InspectorNodeModel(name, "<reference>", type.Name, initiallyExpanded: initiallyExpanded);
            }
        }

        HashSet<object> childVisited = CloneVisited(visited);
        return new InspectorNodeModel(
            name,
            GetTypeDisplayName(type),
            type.Name,
            nodePath,
            initiallyExpanded: initiallyExpanded,
            childFactory: () => BuildObjectChildren(value, accessor, nodePath, depth + 1, childVisited, onValueCommitted, getOwningAssetEntry, getOwningPartition),
            getCurrentValue: getCurrentValue,
            setValueFromText: setValueFromText,
            onValueCommitted: onValueCommitted,
            valueAccessor: accessor,
            getOwningAssetEntry: getOwningAssetEntry,
            getOwningPartition: getOwningPartition,
            expandInlinePointerChildren: options.ExpandInternalPointerChildren,
            hideCollectionChildren: options.HideChildren,
            pointerBaseType: options.PointerBaseType,
            depth: depth);
    }

    private static IEnumerable<InspectorNodeModel> BuildObjectChildren(object value, IInspectorValueAccessor? ownerAccessor, string parentPath, int depth, HashSet<object> visited, Action<InspectorNodeModel>? onValueCommitted, Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry, Func<EbxPartition?>? getOwningPartition)
    {
        Type type = value.GetType();
        List<InspectorNodeModel> children = [];
        foreach (PropertyInfo property in GetOrderedProperties(type))
        {
            if (property.GetCustomAttribute<IsHiddenAttribute>() is not null)
            {
                continue;
            }

            if (ShouldHideRootMetadataProperty(parentPath, property))
            {
                continue;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception ex)
            {
                children.Add(new InspectorNodeModel(GetPropertyDisplayName(property), $"<error: {ex.Message}>", property.PropertyType.Name));
                continue;
            }

            bool isReference = HasAttribute(property, "IsReferenceAttribute");
            bool hideChildren = HasAttribute(property, "HideChildrentAttribute");
            bool isExpandedByDefault = HasAttribute(property, "IsExpandedByDefaultAttribute");
            bool isReadOnly = property.GetCustomAttribute<IsReadOnlyAttribute>() is not null || HasAttribute(property, "FixedSizeArrayAttribute");
            InspectorBuildOptions options = new(
                ExpandInternalPointerChildren: !isReference,
                HideChildren: hideChildren,
                PointerBaseType: property.GetCustomAttribute<EbxFieldMetaAttribute>()?.BaseType);

            children.Add(BuildNode(
                GetPropertyDisplayName(property),
                propertyValue,
                CombinePath(parentPath, property.Name),
                depth,
                CloneVisited(visited),
                onValueCommitted,
                new PropertyValueAccessor(value, property, ownerAccessor, canWrite: !isReadOnly),
                initiallyExpanded: isExpandedByDefault,
                getOwningAssetEntry: getOwningAssetEntry,
                getOwningPartition: getOwningPartition,
                options: options));
        }

        return children;
    }

    private static IEnumerable<InspectorNodeModel> BuildEnumerableChildren(IEnumerable enumerable, IInspectorValueAccessor? ownerAccessor, string parentPath, int depth, HashSet<object> visited, Action<InspectorNodeModel>? onValueCommitted, Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry, Func<EbxPartition?>? getOwningPartition, InspectorBuildOptions options)
    {
        int index = 0;
        if (enumerable is IList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                yield return BuildNode(
                    $"[{i}]",
                    list[i],
                    CombinePath(parentPath, $"[{i}]"),
                    depth,
                    CloneVisited(visited),
                    onValueCommitted,
                    new ListItemValueAccessor(list, i, ownerAccessor?.CanWrite ?? !list.IsReadOnly),
                    options: options,
                    getOwningAssetEntry: getOwningAssetEntry,
                    getOwningPartition: getOwningPartition);
            }

            yield break;
        }

        foreach (object? item in enumerable)
        {
            yield return BuildNode($"[{index}]", item, CombinePath(parentPath, $"[{index}]"), depth, CloneVisited(visited), onValueCommitted, null, getOwningAssetEntry: getOwningAssetEntry, getOwningPartition: getOwningPartition, options: options);
            index++;
        }
    }

    private static IEnumerable<PropertyInfo> GetOrderedProperties(Type type)
    {
        return type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => HasLegacyPropertyOrder(type, property.Name) ? 0 : 1)
            .ThenBy(property => GetLegacyPropertyOrder(type, property.Name) ?? int.MaxValue)
            .ThenBy(property => GetPropertyDisplayName(property), NaturalPropertyNameComparer.Instance)
            .ThenBy(GetFieldSortOrder)
            .ThenBy(static property => property.MetadataToken);
    }

    private static int GetFieldSortOrder(PropertyInfo property)
    {
        return property.GetCustomAttribute<FieldIndexAttribute>()?.Index ?? int.MaxValue;
    }

    private sealed class NaturalPropertyNameComparer : IComparer<string>
    {
        public static NaturalPropertyNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int xIndex = 0;
            int yIndex = 0;
            while (xIndex < x.Length && yIndex < y.Length)
            {
                char xChar = x[xIndex];
                char yChar = y[yIndex];

                if (char.IsDigit(xChar) && char.IsDigit(yChar))
                {
                    long xNumber = ReadNumber(x, ref xIndex);
                    long yNumber = ReadNumber(y, ref yIndex);
                    int numberCompare = xNumber.CompareTo(yNumber);
                    if (numberCompare != 0)
                    {
                        return numberCompare;
                    }

                    continue;
                }

                int charCompare = char.ToUpperInvariant(xChar).CompareTo(char.ToUpperInvariant(yChar));
                if (charCompare != 0)
                {
                    return charCompare;
                }

                xIndex++;
                yIndex++;
            }

            return x.Length.CompareTo(y.Length);
        }

        private static long ReadNumber(string value, ref int index)
        {
            long number = 0;
            while (index < value.Length && char.IsDigit(value[index]))
            {
                number = (number * 10) + (value[index] - '0');
                index++;
            }

            return number;
        }
    }

    private static bool HasLegacyPropertyOrder(Type type, string propertyName)
    {
        return GetLegacyPropertyOrder(type, propertyName).HasValue;
    }

    private static int? GetLegacyPropertyOrder(Type type, string propertyName)
    {
        if (TryGetIndexedSuffix(propertyName, "material_", out int materialIndex))
        {
            return 200 + materialIndex;
        }

        if (TryGetIndexedSuffix(propertyName, "overlay_", out int overlayIndex))
        {
            return 400 + overlayIndex;
        }

        if (type.Name.Contains("LayerComp", StringComparison.OrdinalIgnoreCase))
        {
            if (s_layerCompPropertyOrder.TryGetValue(propertyName, out int fixedOrder))
            {
                return fixedOrder;
            }

            if (type.Name.Contains("Overlay", StringComparison.OrdinalIgnoreCase) &&
                s_layerCompOverlayPropertyOrder.TryGetValue(propertyName, out int overlayOrder))
            {
                return overlayOrder;
            }

            if (type.Name.Contains("Material", StringComparison.OrdinalIgnoreCase) &&
                s_layerCompMaterialPropertyOrder.TryGetValue(propertyName, out int materialOrder))
            {
                return materialOrder;
            }

            if (type.Name.Contains("Maps", StringComparison.OrdinalIgnoreCase) &&
                s_layerCompMapsPropertyOrder.TryGetValue(propertyName, out int mapsOrder))
            {
                return mapsOrder;
            }
        }

        return null;
    }

    private static bool TryGetIndexedSuffix(string propertyName, string prefix, out int value)
    {
        value = 0;
        if (!propertyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(propertyName[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string GetPropertyDisplayName(PropertyInfo property)
    {
        return property.GetCustomAttribute<DisplayNameAttribute>()?.Name ?? property.Name;
    }

    private static bool ShouldHideRootMetadataProperty(string parentPath, PropertyInfo property)
    {
        if (!string.Equals(parentPath, "Data", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(property.Name, "Guid", StringComparison.Ordinal) ||
               string.Equals(property.Name, "__InstanceGuid", StringComparison.Ordinal);
    }

    private static bool HasAttribute(MemberInfo member, string attributeTypeName)
    {
        return member.GetCustomAttributes(false).Any(attribute => string.Equals(attribute.GetType().Name, attributeTypeName, StringComparison.Ordinal));
    }

    private static string GetTypeDisplayName(Type type)
    {
        return type.GetCustomAttribute<DisplayNameAttribute>()?.Name ?? type.Name;
    }

    private static Func<string, InspectorSetResult>? CreateTextSetter(IInspectorValueAccessor? accessor)
    {
        if (accessor is null || !accessor.CanWrite)
        {
            return null;
        }

        Type normalizedType = accessor.GetNormalizedValue()?.GetType() ?? accessor.ValueType;
        if (!IsEditableLeafType(normalizedType))
        {
            return null;
        }

        return text =>
        {
            try
            {
                object? convertedValue = ConvertFromText(text, normalizedType);
                accessor.SetValue(convertedValue);
                return InspectorSetResult.FromValue(accessor.GetNormalizedValue());
            }
            catch (Exception ex)
            {
                return InspectorSetResult.Failure(ex.Message);
            }
        };
    }

    private static bool IsEditableLeafType(Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (actualType.IsEnum || actualType.IsPrimitive)
        {
            return true;
        }

        return actualType == typeof(string) ||
               actualType == typeof(decimal) ||
               actualType == typeof(Guid) ||
               actualType == typeof(DateTime) ||
               actualType == typeof(DateTimeOffset) ||
               actualType == typeof(TimeSpan);
    }

    private static object? ConvertFromText(string text, Type type)
    {
        Type actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (actualType == typeof(string))
        {
            return text;
        }

        if (actualType.IsEnum)
        {
            return Enum.Parse(actualType, text, ignoreCase: true);
        }

        if (actualType == typeof(bool))
        {
            if (bool.TryParse(text, out bool boolean))
            {
                return boolean;
            }

            if (text == "0")
            {
                return false;
            }

            if (text == "1")
            {
                return true;
            }

            throw new FormatException("Boolean values must be True, False, 1, or 0.");
        }

        if (actualType == typeof(Guid))
        {
            return Guid.Parse(text);
        }

        if (actualType == typeof(DateTime))
        {
            return DateTime.Parse(text, CultureInfo.InvariantCulture);
        }

        if (actualType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        }

        if (actualType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        }

        if (actualType == typeof(byte) && TryParseInteger(text, out ulong byteValue))
        {
            return checked((byte)byteValue);
        }

        if (actualType == typeof(sbyte) && TryParseSignedInteger(text, out long sbyteValue))
        {
            return checked((sbyte)sbyteValue);
        }

        if (actualType == typeof(short) && TryParseSignedInteger(text, out long shortValue))
        {
            return checked((short)shortValue);
        }

        if (actualType == typeof(ushort) && TryParseInteger(text, out ulong ushortValue))
        {
            return checked((ushort)ushortValue);
        }

        if (actualType == typeof(int) && TryParseSignedInteger(text, out long intValue))
        {
            return checked((int)intValue);
        }

        if (actualType == typeof(uint) && TryParseInteger(text, out ulong uintValue))
        {
            return checked((uint)uintValue);
        }

        if (actualType == typeof(long) && TryParseSignedInteger(text, out long longValue))
        {
            return longValue;
        }

        if (actualType == typeof(ulong) && TryParseInteger(text, out ulong ulongValue))
        {
            return ulongValue;
        }

        if (actualType == typeof(float))
        {
            return float.Parse(text, CultureInfo.InvariantCulture);
        }

        if (actualType == typeof(double))
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        if (actualType == typeof(decimal))
        {
            return decimal.Parse(text, CultureInfo.InvariantCulture);
        }

        return Convert.ChangeType(text, actualType, CultureInfo.InvariantCulture);
    }

    private static bool TryParseInteger(string text, out ulong value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseSignedInteger(string text, out long value)
    {
        text = text.Trim();
        if (text.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            bool success = long.TryParse(text[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long parsed);
            value = -parsed;
            return success;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            bool success = ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsed);
            value = unchecked((long)parsed);
            return success;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static HashSet<object> CloneVisited(HashSet<object> visited)
    {
        return new HashSet<object>(visited, ReferenceEqualityComparer.Instance);
    }

    private static int GetEnumerableCount(IEnumerable enumerable)
    {
        if (enumerable is ICollection collection)
        {
            return collection.Count;
        }

        int count = 0;
        foreach (object? _ in enumerable)
        {
            count++;
        }

        return count;
    }

    private static bool IsLeafValue(Type type, object value)
    {
        if (type.IsEnum || type.IsPrimitive)
        {
            return true;
        }

        return value is string or decimal or Guid or DateTime or DateTimeOffset or TimeSpan;
    }

    private static string FormatLeafValue(object value)
    {
        return value switch
        {
            string s => s,
            bool b => b ? "True" : "False",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static bool TryBuildSpecialLeaf(string name, object value, string nodePath, out InspectorNodeModel? node)
    {
        switch (value)
        {
            case ResourceRef resourceRef:
                node = new InspectorNodeModel(name, ((ulong)resourceRef).ToString("X16", CultureInfo.InvariantCulture), nameof(ResourceRef), nodePath);
                return true;
            case AssetClassGuid assetClassGuid:
                node = new InspectorNodeModel(name, assetClassGuid.ToString(), nameof(AssetClassGuid), nodePath);
                return true;
            default:
                node = null;
                return false;
        }
    }

    private static string FormatPointer(PointerRef pointer, Func<Frosty.Sdk.Managers.Entries.EbxAssetEntry?>? getOwningAssetEntry)
    {
        return InspectorNodeModel.FormatPointerDisplay(pointer, getOwningAssetEntry);
    }

    private static string CombinePath(string parentPath, string name)
    {
        return string.IsNullOrWhiteSpace(parentPath) ? name : $"{parentPath}.{name}";
    }
}
