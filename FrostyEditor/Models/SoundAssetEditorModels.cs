using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FrostyEditor.Models.Audio;

namespace FrostyEditor.Models;

public enum SoundEditorMode
{
    Variations = 0,
    DataSets = 1
}

public enum SoundPlaybackState
{
    Idle = 0,
    Playing = 1,
    Paused = 2,
    Completed = 3,
    Stopped = 4,
    Error = 5
}

public sealed partial class SoundVariationItemModel : ObservableObject
{
    public object? SourceObject { get; init; }

    public int ListIndex { get; init; }

    public int Index { get; init; }

    public uint VariationId { get; init; }

    public string Name { get; set; } = string.Empty;

    public string SelectionText { get; set; } = string.Empty;

    public string DurationText { get; set; } = string.Empty;

    public string CodecText { get; set; } = string.Empty;

    public string SegmentCountText { get; set; } = string.Empty;

    public string ChunkText { get; set; } = string.Empty;

    public string SampleRateText { get; set; } = string.Empty;

    public ObservableCollection<SoundSegmentItemModel> Segments { get; } = [];

    [ObservableProperty]
    private string m_playbackSummaryText = "Ready";

    [ObservableProperty]
    private bool m_hasActivePlayback;

    [ObservableProperty]
    private bool m_isSelected;
}

public sealed partial class SoundSegmentItemModel : ObservableObject
{
    public object? SourceObject { get; init; }

    public int VariationListIndex { get; init; }

    public int SegmentListIndex { get; init; }

    public int Index { get; init; }

    public uint VariationId { get; init; }

    public string Name { get; set; } = string.Empty;

    public string SegmentDisplayText { get; set; } = string.Empty;

    public string VariationDisplayText { get; set; } = string.Empty;

    public string CodecText { get; set; } = string.Empty;

    public string DurationText { get; set; } = string.Empty;

    public string ChannelText { get; set; } = string.Empty;

    public string SampleRateText { get; set; } = string.Empty;

    public string FlagsText { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    [ObservableProperty]
    private bool m_isSelected;

    [ObservableProperty]
    private bool m_isPlaying;

    [ObservableProperty]
    private bool m_isPaused;

    [ObservableProperty]
    private double m_playProgress;

    [ObservableProperty]
    private string m_elapsedText = "00:00";

    [ObservableProperty]
    private string m_playbackStateText = "Ready";

    [ObservableProperty]
    private double m_progressMaximum = 1;

    [ObservableProperty]
    private SoundPlaybackState m_playbackState = SoundPlaybackState.Idle;

    [ObservableProperty]
    private bool m_isCurrent;

    public double ProgressValue
    {
        get => PlayProgress;
        set => PlayProgress = value;
    }

    public string PlayButtonText => IsPlaying && !IsPaused ? "Pause" : (IsPaused ? "Resume" : "Play");

    public string TotalText => DurationText;

    public string StopButtonText => "Stop";

    partial void OnIsPlayingChanged(bool value)
    {
        if (!value)
        {
            IsPaused = false;
            PlayProgress = 0;
            ElapsedText = "00:00";
        }

        OnPropertyChanged(nameof(PlayButtonText));
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (value)
        {
            IsPlaying = true;
        }

        OnPropertyChanged(nameof(PlayButtonText));
    }

    public void ResetPlaybackState()
    {
        IsPlaying = false;
        IsPaused = false;
        PlaybackState = SoundPlaybackState.Idle;
        PlaybackStateText = "Ready";
        ElapsedText = "0:00.000";
        ProgressValue = 0;
        ProgressMaximum = DurationSeconds > 0 ? DurationSeconds : 1;
        IsCurrent = false;
    }
}

public sealed class SoundDatasetColumnModel
{
    public object? SourceObject { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Header { get; init; } = string.Empty;

    public double Width { get; init; } = 180;

    public bool IsEditable { get; init; }

    public FieldType FieldType { get; init; }

    public int CellIndex { get; init; }

    public bool IsBoolean => FieldType == FieldType.Boolean;

    public FieldType DataType => FieldType;
}

public sealed partial class SoundDatasetCellModel : ObservableObject
{
    private Action<object>? m_commitValue;
    private bool m_suppressCommit;
    private object? m_committedValue;

    public object? SourceObject { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Header { get; init; } = string.Empty;

    public double Width { get; init; } = 180;

    public int RowIndex { get; init; }

    public bool IsEditable { get; init; }

    public FieldType FieldType { get; init; }

    public bool IsBoolean => FieldType == FieldType.Boolean;

    public bool UsesTextEditor => !IsBoolean;

    public object? CommittedValue => m_committedValue;

    public object? OriginalValue => m_committedValue;

    public FieldType DataType => FieldType;

    [ObservableProperty]
    private string m_editorValue = string.Empty;

    [ObservableProperty]
    private string m_valueText = string.Empty;

    [ObservableProperty]
    private string m_originalValueText = string.Empty;

    [ObservableProperty]
    private bool m_booleanValue;

    [ObservableProperty]
    private bool m_isDirty;

    [ObservableProperty]
    private bool m_isSelected;

    [ObservableProperty]
    private bool m_hasValidationError;

    [ObservableProperty]
    private string m_validationMessage = string.Empty;

    public SoundDatasetCellModel()
    {
    }

    public SoundDatasetCellModel(object? initialValue, Action<object>? commitValue)
    {
        m_commitValue = commitValue;
        SetCommittedValue(initialValue, notify: false);
    }

    public void SetCommitCallback(Action<object>? commitValue)
    {
        m_commitValue = commitValue;
    }

    public string EditedValueText
    {
        get => EditorValue;
        set => EditorValue = value;
    }

    public string ValidationError
    {
        get => ValidationMessage;
        set
        {
            ValidationMessage = value;
            HasValidationError = !string.IsNullOrWhiteSpace(value);
        }
    }

    partial void OnEditorValueChanged(string value)
    {
        ValueText = value;
        IsDirty = !string.Equals(value, OriginalValueText, StringComparison.Ordinal);
        if (m_suppressCommit || IsBoolean || !IsEditable)
        {
            return;
        }

        TryCommit(value);
    }

    partial void OnBooleanValueChanged(bool value)
    {
        if (IsBoolean)
        {
            ValueText = value ? "True" : "False";
            IsDirty = !Equals(m_committedValue, value);
        }

        if (m_suppressCommit || !IsBoolean || !IsEditable)
        {
            return;
        }

        CommitValue(value);
    }

    public void SetCommittedValue(object? value, bool notify = true)
    {
        m_suppressCommit = true;
        try
        {
            m_committedValue = value;
            HasValidationError = false;
            ValidationMessage = string.Empty;

            if (IsBoolean)
            {
                BooleanValue = value is bool boolean && boolean;
                ValueText = BooleanValue ? "True" : "False";
            }
            else
            {
                EditorValue = FormatValue(value);
                ValueText = EditorValue;
            }

            OriginalValueText = FormatValue(value);
            IsDirty = false;
        }
        finally
        {
            m_suppressCommit = false;
        }

        if (notify)
        {
            OnPropertyChanged(nameof(CommittedValue));
        }
    }

    public void AcceptValue(object? value, string formattedValue)
    {
        SetCommittedValue(value, notify: false);
        OriginalValueText = formattedValue;
        ValueText = formattedValue;
        EditorValue = formattedValue;
        IsDirty = false;
        ValidationError = string.Empty;
    }

    public void RejectEdit()
    {
        SetCommittedValue(m_committedValue, notify: false);
        ValidationError = string.Empty;
    }

    public bool Validate()
    {
        if (IsBoolean)
        {
            return true;
        }

        return TryCommit(EditorValue);
    }

    private bool TryCommit(string value)
    {
        try
        {
            object converted = ParseValue(FieldType, value);
            CommitValue(converted);
            return true;
        }
        catch (Exception ex)
        {
            HasValidationError = true;
            ValidationMessage = ex.Message;
            return false;
        }
    }

    private void CommitValue(object value)
    {
        m_committedValue = value;
        HasValidationError = false;
        ValidationMessage = string.Empty;
        m_commitValue?.Invoke(value);
        OnPropertyChanged(nameof(CommittedValue));
    }

    private static object ParseValue(FieldType fieldType, string value)
    {
        return fieldType switch
        {
            FieldType.Boolean => bool.TryParse(value, out bool boolean)
                ? boolean
                : throw new FormatException("Expected True or False."),
            FieldType.Int32 => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            FieldType.Int64 => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            FieldType.UInt32 => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            FieldType.UInt64 => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            FieldType.Float32 => float.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            FieldType.Float64 => double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            FieldType.String => value,
            FieldType.Pointer => Guid.Parse(value),
            _ => throw new NotSupportedException($"The field type '{fieldType}' is not supported.")
        };
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            bool boolean => boolean ? "True" : "False",
            float single => single.ToString("0.###", CultureInfo.InvariantCulture),
            double dbl => dbl.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }
}

public sealed partial class SoundDatasetRowModel : ObservableObject
{
    public object? SourceObject { get; init; }

    [ObservableProperty]
    private int m_index;

    [ObservableProperty]
    private string m_name = string.Empty;

    [ObservableProperty]
    private bool m_isDirty;

    [ObservableProperty]
    private bool m_isNewRow;

    [ObservableProperty]
    private string m_stateText = "Clean";

    public bool HasValidationError => Cells.Any(static cell => cell.HasValidationError);

    public int DisplayIndex => Index + 1;

    public ObservableCollection<SoundDatasetCellModel> Cells { get; } = [];

    public void RefreshValidationState()
    {
        OnPropertyChanged(nameof(HasValidationError));
    }

    partial void OnIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayIndex));
    }
}

public sealed partial class SoundDatasetSheetModel : ObservableObject
{
    public object? SourceObject { get; init; }

    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string m_displayName = string.Empty;

    [ObservableProperty]
    private string m_rowCountText = "0 rows";

    [ObservableProperty]
    private bool m_hasPendingChanges;

    [ObservableProperty]
    private string m_pendingChangesText = "No pending changes";

    [ObservableProperty]
    private bool m_canAddRow;

    [ObservableProperty]
    private bool m_isLoaded;

    [ObservableProperty]
    private int m_rowCount;

    public ObservableCollection<SoundDatasetColumnModel> Columns { get; } = [];

    public ObservableCollection<SoundDatasetRowModel> Rows { get; } = [];

    public void RefreshRowCount()
    {
        RowCount = Rows.Count;
        RowCountText = $"{RowCount:N0} row(s)";
    }
}
