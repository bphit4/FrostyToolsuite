#nullable disable
#pragma warning disable CS8632
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.Managers;
using FrostyEditor.Models;
using FrostyEditor.Models.Audio;
using FrostyEditor.Utils;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FrostyEditor.ViewModels;

public class SoundAssetEditorViewModel : AssetEditorViewModel, ISessionStateAwareDocument, ISaveableDocument
{
	private sealed class AveragedStereoSampleProvider : ISampleProvider
	{
		private readonly ISampleProvider m_source;

		private readonly int m_sourceChannels;

		private float[] m_sourceBuffer = Array.Empty<float>();

		public WaveFormat WaveFormat { get; }

		public AveragedStereoSampleProvider(ISampleProvider source)
		{
			m_source = source;
			m_sourceChannels = Math.Max(1, source.WaveFormat.Channels);
			WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
		}

		public int Read(float[] buffer, int offset, int count)
		{
			if (count <= 0)
			{
				return 0;
			}
			int num = (count + 1) / 2;
			int num2 = num * m_sourceChannels;
			if (m_sourceBuffer.Length < num2)
			{
				m_sourceBuffer = new float[num2];
			}
			int num3 = m_source.Read(m_sourceBuffer, 0, num2);
			int num4 = num3 / m_sourceChannels;
			int num5 = 0;
			for (int i = 0; i < num4; i++)
			{
				if (num5 + 1 >= count)
				{
					break;
				}
				int num6 = i * m_sourceChannels;
				float num7 = 0f;
				for (int j = 0; j < m_sourceChannels; j++)
				{
					num7 += m_sourceBuffer[num6 + j];
				}
				float num8 = num7 / (float)m_sourceChannels;
				buffer[offset + num5++] = num8;
				buffer[offset + num5++] = num8;
			}
			return num5;
		}
	}

	private sealed class SoundEditorLoadSnapshot
	{
		public required SoundAssetState State { get; init; }

		public required string SourceText { get; init; }

		public required string BankSummaryText { get; init; }

		public required string OperationsText { get; init; }

		public required string CapabilitySummaryText { get; init; }

		public required List<SoundVariationItemModel> Variations { get; init; }

		public required List<SoundSegmentItemModel> SegmentRows { get; init; }

		public required List<SoundDatasetSheetModel> DataSets { get; init; }

		public required bool IsModified { get; init; }
	}

	private sealed record ImportedDataSetRow(IReadOnlyDictionary<string, object?> Values);

	private static readonly object s_playbackOwnershipLock = new object();

	private static WeakReference<SoundAssetEditorViewModel>? s_activePlaybackOwner;

	private readonly EbxAssetEntry m_entryTyped;

	private readonly DispatcherTimer m_playbackTimer;

	private SoundAssetState? m_state;

	private IWavePlayer? m_waveOut;

	private AudioFileReader? m_audioReader;

	private string? m_previewPath;

	private SoundSegmentItemModel? m_playingSegment;

	private SoundVariationItemModel? m_playingVariation;

	private readonly Dictionary<(Guid ChunkId, uint Offset), SpsSoundHeader?> m_segmentHeaderCache = new Dictionary<(Guid, uint), SpsSoundHeader>();

	private bool m_isSeeking;

	private bool m_stopRequested;

	private int m_reloadGeneration;
	private SoundEditorMode m_mode = SoundEditorMode.Variations;
	private SoundVariationItemModel? m_selectedVariation;
	private SoundSegmentItemModel? m_selectedSegment;
	private SoundDatasetSheetModel? m_selectedDataSet;
	private SoundDatasetRowModel? m_selectedDataSetRow;
	private string m_statusText = "Loading sound editor...";
	private string m_sourceText = "Source: unresolved";
	private string m_operationsText = "Preview and export are available.";
	private string m_bankSummaryText = "No parsed sound bank is available for this asset.";
	private string m_selectionSummaryText = "Select a variation, segment, or data set.";
	private string m_selectedVariationSummaryText = "Select a variation.";
	private string m_selectedSegmentSummaryText = "Select a segment.";
	private string m_selectedDataSetSummaryText = "Select a data set.";
	private string m_selectedDataSetRowSummaryText = "Select a row.";
	private string m_capabilitySummaryText = "Preview, per-segment import/export, bulk segment import/export, and data-set editing are available.";
	private string m_playbackStateText = "Idle";
	private string m_playbackTargetText = "No preview active.";
	private string m_playbackTimelineText = "0:00.000 / 0:00.000";
	private string m_dataSetEditorSummaryText = "Select a data set row to inspect or edit.";
	private double m_playbackPosition;
	private double m_playbackMaximum = 1.0;
	private bool m_isPlaybackActive;
	private bool m_hasPendingDataSetEdits;
	private bool m_isModified;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private RelayCommand? toggleModeCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? playCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand<SoundSegmentItemModel?>? playSegmentCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private RelayCommand? pausePlaybackCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private RelayCommand? resumePlaybackCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private RelayCommand? stopPlaybackCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private RelayCommand<SoundSegmentItemModel?>? stopSegmentCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? playPreviousSegmentCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? playNextSegmentCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? exportCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand<SoundSegmentItemModel?>? exportSegmentCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? exportBulkCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? importCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand<SoundSegmentItemModel?>? importSegmentCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? importBulkCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? exportDataSetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? importDataSetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? saveCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? addCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private RelayCommand? removeCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	private AsyncRelayCommand? revertCommand;

	public ObservableCollection<SoundVariationItemModel> Variations { get; } = new ObservableCollection<SoundVariationItemModel>();

	public ObservableCollection<SoundSegmentItemModel> SegmentRows { get; } = new ObservableCollection<SoundSegmentItemModel>();

	public ObservableCollection<SoundSegmentItemModel> SelectedVariationSegments { get; } = new ObservableCollection<SoundSegmentItemModel>();

	public ObservableCollection<SoundDatasetSheetModel> DataSets { get; } = new ObservableCollection<SoundDatasetSheetModel>();

	public ObservableCollection<SoundDatasetColumnModel> SelectedDataSetColumns { get; } = new ObservableCollection<SoundDatasetColumnModel>();

	public ObservableCollection<SoundDatasetRowModel> SelectedDataSetRows { get; } = new ObservableCollection<SoundDatasetRowModel>();

	public bool IsVariationsMode => Mode == SoundEditorMode.Variations;

	public bool IsDataSetsMode => Mode == SoundEditorMode.DataSets;

	public bool CanPlaySelection => SelectedSegment != null;

	public bool CanExportSelection => IsDataSetsMode ? (SelectedDataSet != null) : (SelectedSegment != null);

	public bool CanImportSelection => IsDataSetsMode ? (SelectedDataSet != null) : (SelectedSegment != null);

	public bool CanSaveAsset => HasPendingDataSetEdits;

	public bool CanAddItem
	{
		get
		{
			SoundAssetState? state = m_state;
			return (object)state != null && state.Kind == SoundAssetKind.Bank && (IsVariationsMode || SelectedDataSet != null);
		}
	}

	public bool CanRemoveItem => IsDataSetsMode && SelectedDataSetRow != null && SelectedDataSet != null;

	public bool CanStopPlayback => IsPlaybackActive;

	public bool CanPausePlayback
	{
		get
		{
			IWavePlayer? waveOut = m_waveOut;
			return waveOut != null && waveOut.PlaybackState == PlaybackState.Playing;
		}
	}

	public bool CanResumePlayback
	{
		get
		{
			IWavePlayer? waveOut = m_waveOut;
			return waveOut != null && waveOut.PlaybackState == PlaybackState.Paused;
		}
	}

	public bool CanPlayPreviousSegment => SelectedSegment != null && SegmentRows.IndexOf(SelectedSegment) > 0;

	public bool CanPlayNextSegment => SelectedSegment != null && SegmentRows.IndexOf(SelectedSegment) >= 0 && SegmentRows.IndexOf(SelectedSegment) < SegmentRows.Count - 1;

	public bool CanExportBulk
	{
		get
		{
			SoundAssetState? state = m_state;
			return (object)state != null && state.Kind == SoundAssetKind.Bank && m_state.ParsedBank != null;
		}
	}

	public bool CanImportBulk
	{
		get
		{
			SoundAssetState? state = m_state;
			return (object)state != null && state.Kind == SoundAssetKind.Bank && m_state.ParsedBank != null;
		}
	}

	public bool CanExportDataSet => SelectedDataSet != null;

	public bool CanImportDataSet => SelectedDataSet != null;

	public bool HasValidationErrors => DataSets.Any((SoundDatasetSheetModel dataSet) => dataSet.Rows.Any((SoundDatasetRowModel row) => row.HasValidationError));

	public bool IsPlaybackProgressIndeterminate => PlaybackMaximum <= 0.0;

	public bool CanToggleMode => DataSets.Count > 0 || (m_state?.ParsedBank?.AllDataSets.Count).GetValueOrDefault() > 0;

	public string ModeButtonText => IsVariationsMode ? "Switch to Data Set mode" : "Switch to Variations mode";

	public string ModeHeaderText => IsVariationsMode ? "Variations / Segments" : "Data Sets";

	public string AddButtonText => IsDataSetsMode ? "Add Row" : "Add New Sound";

	public string SaveButtonText => "Save";

	public string VariationCountText => $"{SegmentRows.Count:N0} segment(s)";

	public string DataSetCountText => $"{Math.Max(DataSets.Count, (m_state?.ParsedBank?.AllDataSets.Count).GetValueOrDefault()):N0} data set(s)";

	public string SelectedVariationSegmentCountText => $"{SelectedVariationSegments.Count:N0} segment(s)";

	public string SelectedDataSetRowCountText => SelectedDataSet?.RowCountText ?? "0 row(s)";

	public bool HasUnsavedChanges => HasPendingDataSetEdits;

	public bool CanSaveDocument => HasPendingDataSetEdits && !HasValidationErrors;

	public bool CanExportDocument => IsDataSetsMode ? (SelectedDataSet != null) : (SelectedSegment != null);

	public string DirtyStateText => HasPendingDataSetEdits ? "Unsaved data-set edits" : (IsModified ? "Saved to session" : "Saved");

	public bool? AllSegmentsSelected
	{
		get
		{
			if (SegmentRows.Count == 0)
			{
				return false;
			}
			if (SegmentRows.All((SoundSegmentItemModel segment) => segment.IsSelected))
			{
				return true;
			}
			if (SegmentRows.All((SoundSegmentItemModel segment) => !segment.IsSelected))
			{
				return false;
			}
			return null;
		}
		set
		{
			bool valueOrDefault = value == true;
			foreach (SoundSegmentItemModel segmentRow in SegmentRows)
			{
				segmentRow.IsSelected = valueOrDefault;
			}
			RaiseComputedPropertyChanges();
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public SoundEditorMode Mode
	{
		get
		{
			return m_mode;
		}
		set
		{
			if (!EqualityComparer<SoundEditorMode>.Default.Equals(m_mode, value))
			{
				OnPropertyChanging();
				m_mode = value;
				OnModeChanged(value);
				OnPropertyChanged(nameof(Mode));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public SoundVariationItemModel? SelectedVariation
	{
		get
		{
			return m_selectedVariation;
		}
		set
		{
			if (!EqualityComparer<SoundVariationItemModel>.Default.Equals(m_selectedVariation, value))
			{
				OnPropertyChanging();
				m_selectedVariation = value;
				OnSelectedVariationChanged(value);
				OnPropertyChanged(nameof(SelectedVariation));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public SoundSegmentItemModel? SelectedSegment
	{
		get
		{
			return m_selectedSegment;
		}
		set
		{
			if (!EqualityComparer<SoundSegmentItemModel>.Default.Equals(m_selectedSegment, value))
			{
				OnPropertyChanging();
				m_selectedSegment = value;
				OnSelectedSegmentChanged(value);
				OnPropertyChanged(nameof(SelectedSegment));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public SoundDatasetSheetModel? SelectedDataSet
	{
		get
		{
			return m_selectedDataSet;
		}
		set
		{
			if (!EqualityComparer<SoundDatasetSheetModel>.Default.Equals(m_selectedDataSet, value))
			{
				OnPropertyChanging();
				m_selectedDataSet = value;
				OnSelectedDataSetChanged(value);
				OnPropertyChanged(nameof(SelectedDataSet));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public SoundDatasetRowModel? SelectedDataSetRow
	{
		get
		{
			return m_selectedDataSetRow;
		}
		set
		{
			if (!EqualityComparer<SoundDatasetRowModel>.Default.Equals(m_selectedDataSetRow, value))
			{
				OnPropertyChanging();
				m_selectedDataSetRow = value;
				OnSelectedDataSetRowChanged(value);
				OnPropertyChanged(nameof(SelectedDataSetRow));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string StatusText
	{
		get
		{
			return m_statusText;
		}
		[MemberNotNull("m_statusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_statusText, value))
			{
				OnPropertyChanging();
				m_statusText = value;
				OnPropertyChanged(nameof(StatusText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string SourceText
	{
		get
		{
			return m_sourceText;
		}
		[MemberNotNull("m_sourceText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_sourceText, value))
			{
				OnPropertyChanging();
				m_sourceText = value;
				OnPropertyChanged(nameof(SourceText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string OperationsText
	{
		get
		{
			return m_operationsText;
		}
		[MemberNotNull("m_operationsText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_operationsText, value))
			{
				OnPropertyChanging();
				m_operationsText = value;
				OnPropertyChanged(nameof(OperationsText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string BankSummaryText
	{
		get
		{
			return m_bankSummaryText;
		}
		[MemberNotNull("m_bankSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_bankSummaryText, value))
			{
				OnPropertyChanging();
				m_bankSummaryText = value;
				OnPropertyChanged(nameof(BankSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectionSummaryText
	{
		get
		{
			return m_selectionSummaryText;
		}
		[MemberNotNull("m_selectionSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_selectionSummaryText, value))
			{
				OnPropertyChanging();
				m_selectionSummaryText = value;
				OnPropertyChanged(nameof(SelectionSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedVariationSummaryText
	{
		get
		{
			return m_selectedVariationSummaryText;
		}
		[MemberNotNull("m_selectedVariationSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_selectedVariationSummaryText, value))
			{
				OnPropertyChanging();
				m_selectedVariationSummaryText = value;
				OnPropertyChanged(nameof(SelectedVariationSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedSegmentSummaryText
	{
		get
		{
			return m_selectedSegmentSummaryText;
		}
		[MemberNotNull("m_selectedSegmentSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_selectedSegmentSummaryText, value))
			{
				OnPropertyChanging();
				m_selectedSegmentSummaryText = value;
				OnPropertyChanged(nameof(SelectedSegmentSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedDataSetSummaryText
	{
		get
		{
			return m_selectedDataSetSummaryText;
		}
		[MemberNotNull("m_selectedDataSetSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_selectedDataSetSummaryText, value))
			{
				OnPropertyChanging();
				m_selectedDataSetSummaryText = value;
				OnPropertyChanged(nameof(SelectedDataSetSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedDataSetRowSummaryText
	{
		get
		{
			return m_selectedDataSetRowSummaryText;
		}
		[MemberNotNull("m_selectedDataSetRowSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_selectedDataSetRowSummaryText, value))
			{
				OnPropertyChanging();
				m_selectedDataSetRowSummaryText = value;
				OnPropertyChanged(nameof(SelectedDataSetRowSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string CapabilitySummaryText
	{
		get
		{
			return m_capabilitySummaryText;
		}
		[MemberNotNull("m_capabilitySummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_capabilitySummaryText, value))
			{
				OnPropertyChanging();
				m_capabilitySummaryText = value;
				OnPropertyChanged(nameof(CapabilitySummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string PlaybackStateText
	{
		get
		{
			return m_playbackStateText;
		}
		[MemberNotNull("m_playbackStateText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_playbackStateText, value))
			{
				OnPropertyChanging();
				m_playbackStateText = value;
				OnPropertyChanged(nameof(PlaybackStateText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string PlaybackTargetText
	{
		get
		{
			return m_playbackTargetText;
		}
		[MemberNotNull("m_playbackTargetText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_playbackTargetText, value))
			{
				OnPropertyChanging();
				m_playbackTargetText = value;
				OnPropertyChanged(nameof(PlaybackTargetText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string PlaybackTimelineText
	{
		get
		{
			return m_playbackTimelineText;
		}
		[MemberNotNull("m_playbackTimelineText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_playbackTimelineText, value))
			{
				OnPropertyChanging();
				m_playbackTimelineText = value;
				OnPropertyChanged(nameof(PlaybackTimelineText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public string DataSetEditorSummaryText
	{
		get
		{
			return m_dataSetEditorSummaryText;
		}
		[MemberNotNull("m_dataSetEditorSummaryText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(m_dataSetEditorSummaryText, value))
			{
				OnPropertyChanging();
				m_dataSetEditorSummaryText = value;
				OnPropertyChanged(nameof(DataSetEditorSummaryText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public double PlaybackPosition
	{
		get
		{
			return m_playbackPosition;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(m_playbackPosition, value))
			{
				OnPropertyChanging();
				m_playbackPosition = value;
				OnPlaybackPositionChanged(value);
				OnPropertyChanged(nameof(PlaybackPosition));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public double PlaybackMaximum
	{
		get
		{
			return m_playbackMaximum;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(m_playbackMaximum, value))
			{
				OnPropertyChanging();
				m_playbackMaximum = value;
				OnPropertyChanged(nameof(PlaybackMaximum));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsPlaybackActive
	{
		get
		{
			return m_isPlaybackActive;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(m_isPlaybackActive, value))
			{
				OnPropertyChanging();
				m_isPlaybackActive = value;
				OnPropertyChanged(nameof(IsPlaybackActive));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasPendingDataSetEdits
	{
		get
		{
			return m_hasPendingDataSetEdits;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(m_hasPendingDataSetEdits, value))
			{
				OnPropertyChanging();
				m_hasPendingDataSetEdits = value;
				OnHasPendingDataSetEditsChanged(value);
				OnPropertyChanged(nameof(HasPendingDataSetEdits));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsModified
	{
		get
		{
			return m_isModified;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(m_isModified, value))
			{
				OnPropertyChanging();
				m_isModified = value;
				OnIsModifiedChanged(value);
				OnPropertyChanged(nameof(IsModified));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ToggleModeCommand => toggleModeCommand ?? (toggleModeCommand = new RelayCommand(ToggleMode));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand PlayCommand => playCommand ?? (playCommand = new AsyncRelayCommand(Play));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<SoundSegmentItemModel?> PlaySegmentCommand => playSegmentCommand ?? (playSegmentCommand = new AsyncRelayCommand<SoundSegmentItemModel>(PlaySegment));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand PausePlaybackCommand => pausePlaybackCommand ?? (pausePlaybackCommand = new RelayCommand(PausePlayback));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ResumePlaybackCommand => resumePlaybackCommand ?? (resumePlaybackCommand = new RelayCommand(ResumePlayback));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand StopPlaybackCommand => stopPlaybackCommand ?? (stopPlaybackCommand = new RelayCommand(StopPlayback));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<SoundSegmentItemModel?> StopSegmentCommand => stopSegmentCommand ?? (stopSegmentCommand = new RelayCommand<SoundSegmentItemModel>(StopSegment));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand PlayPreviousSegmentCommand => playPreviousSegmentCommand ?? (playPreviousSegmentCommand = new AsyncRelayCommand(PlayPreviousSegment));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand PlayNextSegmentCommand => playNextSegmentCommand ?? (playNextSegmentCommand = new AsyncRelayCommand(PlayNextSegment));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExportCommand => exportCommand ?? (exportCommand = new AsyncRelayCommand(Export));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<SoundSegmentItemModel?> ExportSegmentCommand => exportSegmentCommand ?? (exportSegmentCommand = new AsyncRelayCommand<SoundSegmentItemModel>(ExportSegment));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExportBulkCommand => exportBulkCommand ?? (exportBulkCommand = new AsyncRelayCommand(ExportBulk));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportCommand => importCommand ?? (importCommand = new AsyncRelayCommand(Import));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<SoundSegmentItemModel?> ImportSegmentCommand => importSegmentCommand ?? (importSegmentCommand = new AsyncRelayCommand<SoundSegmentItemModel>(ImportSegment));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportBulkCommand => importBulkCommand ?? (importBulkCommand = new AsyncRelayCommand(ImportBulk));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExportDataSetCommand => exportDataSetCommand ?? (exportDataSetCommand = new AsyncRelayCommand(ExportDataSet));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportDataSetCommand => importDataSetCommand ?? (importDataSetCommand = new AsyncRelayCommand(ImportDataSet));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveCommand => saveCommand ?? (saveCommand = new AsyncRelayCommand(Save));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand AddCommand => addCommand ?? (addCommand = new AsyncRelayCommand(Add));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand RemoveCommand => removeCommand ?? (removeCommand = new RelayCommand(Remove));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.2.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RevertCommand => revertCommand ?? (revertCommand = new AsyncRelayCommand(Revert));

	public SoundAssetEditorViewModel(EbxAssetEntry entry)
		: base(entry)
	{
		m_entryTyped = entry;
		m_playbackTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(100.0)
		};
		m_playbackTimer.Tick += delegate
		{
			UpdatePlayback();
		};
		ReloadFromSource();
	}

	public static bool IsSoundAsset(EbxAssetEntry entry)
	{
		return SoundAssetOperations.IsSoundAsset(entry);
	}

	public static Task<SoundOperationResult> ExportWithPickerAsync(EbxAssetEntry entry)
	{
		return SoundAssetOperations.ExportWithPickerAsync(entry);
	}

	public static Task<SoundOperationResult> ImportWithPickerAsync(EbxAssetEntry entry)
	{
		return SoundAssetOperations.ImportWithPickerAsync(entry);
	}

	public static Task<SoundOperationResult> RevertAsync(EbxAssetEntry entry)
	{
		return SoundAssetOperations.RevertAsync(entry);
	}

	public async Task<bool> SaveDocumentAsync()
	{
		return (await SaveCoreAsync().ConfigureAwait(continueOnCapturedContext: true)).Success && !HasPendingDataSetEdits;
	}

	public async Task<bool> ExportDocumentAsync()
	{
		SoundOperationResult soundOperationResult = ((!IsDataSetsMode) ? (await ExportSelectionCoreAsync().ConfigureAwait(continueOnCapturedContext: true)) : (await ExportDataSetCoreAsync().ConfigureAwait(continueOnCapturedContext: true)));
		SoundOperationResult result = soundOperationResult;
		return result.Success;
	}

	public void RefreshSessionState()
	{
		IsModified = SoundAssetOperations.IsModified(m_entryTyped);
		RaiseComputedPropertyChanges();
	}

	public override void ReloadFromSource()
	{
		int? selectedVariationIndex = SelectedVariation?.ListIndex;
		int? selectedSegmentIndex = SelectedSegment?.SegmentListIndex;
		string selectedDataSetId = SelectedDataSet?.Id;
		int? selectedRowIndex = SelectedDataSetRow?.Index;
		SoundEditorMode mode = Mode;
		StopPlaybackInternal(resetState: true);
		m_segmentHeaderCache.Clear();
		StatusText = "Loading sound editor...";
		RaiseComputedPropertyChanges();
		_ = ReloadFromSourceAsync(++m_reloadGeneration, selectedVariationIndex, selectedSegmentIndex, selectedDataSetId, selectedRowIndex, mode);
	}

	private async Task ReloadFromSourceAsync(int generation, int? selectedVariationIndex, int? selectedSegmentIndex, string? selectedDataSetId, int? selectedRowIndex, SoundEditorMode currentMode)
	{
		try
		{
			SoundEditorLoadSnapshot snapshot = await Task.Run(delegate
			{
				SoundAssetState state = SoundAssetOperations.Load(m_entryTyped);
				return BuildLoadSnapshot(state);
			}).ConfigureAwait(continueOnCapturedContext: true);
			if (generation == m_reloadGeneration)
			{
				ApplyLoadSnapshot(snapshot, selectedVariationIndex, selectedSegmentIndex, selectedDataSetId, selectedRowIndex, currentMode);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (generation == m_reloadGeneration)
			{
				m_state = null;
				Variations.Clear();
				SegmentRows.Clear();
				SelectedVariationSegments.Clear();
				DataSets.Clear();
				SelectedDataSetColumns.Clear();
				SelectedDataSetRows.Clear();
				SelectedVariation = null;
				SelectedSegment = null;
				SelectedDataSet = null;
				SelectedDataSetRow = null;
				HasPendingDataSetEdits = false;
				IsModified = false;
				SourceText = "Source: unavailable";
				BankSummaryText = "Unable to load sound data.";
				StatusText = "Failed to load sound editor: " + ex2.Message;
				RaiseComputedPropertyChanges();
			}
		}
	}
	private void ToggleMode()
	{
		if (!CanToggleMode)
		{
			StatusText = "This asset does not expose editable sound data sets.";
		}
		else
		{
			Mode = (IsVariationsMode ? SoundEditorMode.DataSets : SoundEditorMode.Variations);
		}
	}
	private async Task Play()
	{
		SoundSegmentItemModel segment = SelectedSegment ?? SegmentRows.FirstOrDefault();
		if (segment == null)
		{
			StatusText = "Select a sound segment to preview.";
		}
		else
		{
			await PlaySegmentCoreAsync(segment).ConfigureAwait(continueOnCapturedContext: true);
		}
	}
	private async Task PlaySegment(SoundSegmentItemModel? segment)
	{
		if (segment != null)
		{
			await PlaySegmentCoreAsync(segment).ConfigureAwait(continueOnCapturedContext: true);
		}
	}
	private void PausePlayback()
	{
		IWavePlayer? waveOut = m_waveOut;
		if (waveOut != null && waveOut.PlaybackState == PlaybackState.Playing && m_playingSegment != null)
		{
			m_waveOut.Pause();
			m_playingSegment.IsPaused = true;
			m_playingSegment.PlaybackState = SoundPlaybackState.Paused;
			m_playingSegment.PlaybackStateText = "Paused";
			PlaybackStateText = "Paused";
			RaiseComputedPropertyChanges();
		}
	}
	private void ResumePlayback()
	{
		IWavePlayer? waveOut = m_waveOut;
		if (waveOut != null && waveOut.PlaybackState == PlaybackState.Paused && m_playingSegment != null)
		{
			m_waveOut.Play();
			m_playingSegment.IsPaused = false;
			m_playingSegment.IsPlaying = true;
			m_playingSegment.PlaybackState = SoundPlaybackState.Playing;
			m_playingSegment.PlaybackStateText = "Playing";
			PlaybackStateText = "Playing";
			RaiseComputedPropertyChanges();
		}
	}
	private void StopPlayback()
	{
		StopPlaybackInternal(resetState: true);
		StatusText = "Playback stopped.";
	}
	private void StopSegment(SoundSegmentItemModel? segment)
	{
		if (segment != null && segment == m_playingSegment)
		{
			StopPlaybackInternal(resetState: true);
			StatusText = "Playback stopped.";
		}
	}
	private async Task PlayPreviousSegment()
	{
		if (SelectedSegment != null)
		{
			int index = SegmentRows.IndexOf(SelectedSegment);
			if (index > 0)
			{
				SelectedSegment = SegmentRows[index - 1];
				await PlaySegmentCoreAsync(SelectedSegment).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
	}
	private async Task PlayNextSegment()
	{
		if (SelectedSegment != null)
		{
			int index = SegmentRows.IndexOf(SelectedSegment);
			if (index >= 0 && index < SegmentRows.Count - 1)
			{
				SelectedSegment = SegmentRows[index + 1];
				await PlaySegmentCoreAsync(SelectedSegment).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
	}
	private async Task Export()
	{
		SoundOperationResult soundOperationResult = ((!IsDataSetsMode) ? (await ExportSelectionCoreAsync().ConfigureAwait(continueOnCapturedContext: true)) : (await ExportDataSetCoreAsync().ConfigureAwait(continueOnCapturedContext: true)));
		SoundOperationResult result = soundOperationResult;
		StatusText = result.Message;
	}
	private async Task ExportSegment(SoundSegmentItemModel? segment)
	{
		StatusText = (await ExportSegmentCoreAsync(segment ?? SelectedSegment).ConfigureAwait(continueOnCapturedContext: true)).Message;
	}
	private async Task ExportBulk()
	{
		if (!CanExportBulk)
		{
			StatusText = "This sound asset does not expose bank segments for bulk export.";
			return;
		}
		IStorageFolder folder = (await FileService.OpenFoldersAsync(new FolderPickerOpenOptions
		{
			Title = "Choose export folder",
			AllowMultiple = false
		}).ConfigureAwait(continueOnCapturedContext: true))?.FirstOrDefault();
		if (folder == null)
		{
			StatusText = "Bulk sound export canceled.";
		}
		else
		{
			StatusText = (await SoundAssetOperations.ExportBankSegmentsAsync(outputDirectory: System.IO.Path.Combine(folder.Path.LocalPath, SafeName(m_entryTyped.Filename)), entry: m_entryTyped).ConfigureAwait(continueOnCapturedContext: true)).Message;
		}
	}
	private async Task Import()
	{
		SoundOperationResult soundOperationResult = ((!IsDataSetsMode) ? (await ImportSegmentCoreAsync(SelectedSegment).ConfigureAwait(continueOnCapturedContext: true)) : (await ImportDataSetCoreAsync().ConfigureAwait(continueOnCapturedContext: true)));
		SoundOperationResult result = soundOperationResult;
		StatusText = result.Message;
	}
	private async Task ImportSegment(SoundSegmentItemModel? segment)
	{
		StatusText = (await ImportSegmentCoreAsync(segment ?? SelectedSegment).ConfigureAwait(continueOnCapturedContext: true)).Message;
	}

	public void SeekSegmentProgress(SoundSegmentItemModel segment, double progress)
	{
		if (segment == m_playingSegment && !(PlaybackMaximum <= 0.0))
		{
			double playbackPosition = Math.Clamp(progress, 0.0, 1.0) * PlaybackMaximum;
			PlaybackPosition = playbackPosition;
		}
	}
	private async Task ImportBulk()
	{
		if (!CanImportBulk)
		{
			StatusText = "This sound asset does not expose bank segments for bulk import.";
			return;
		}
		SoundOperationResult saveResult = await EnsureSavedBeforeImmediateCommitAsync().ConfigureAwait(continueOnCapturedContext: true);
		if (!saveResult.Success)
		{
			StatusText = saveResult.Message;
			return;
		}
		FilePickerOpenOptions filePickerOpenOptions = new FilePickerOpenOptions();
		filePickerOpenOptions.Title = "Import bank segments";
		filePickerOpenOptions.AllowMultiple = true;
		filePickerOpenOptions.FileTypeFilter = new _003C_003Ez__ReadOnlySingleElementList<FilePickerFileType>(new FilePickerFileType("Audio files")
		{
			Patterns = new _003C_003Ez__ReadOnlyArray<string>(new string[6] { "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a", "*.sps" })
		});
		IReadOnlyList<IStorageFile> files = await FileService.OpenFilesAsync(filePickerOpenOptions).ConfigureAwait(continueOnCapturedContext: true);
		if (files == null || files.Count == 0)
		{
			StatusText = "Bulk sound import canceled.";
			return;
		}
		SoundOperationResult result = await SoundAssetOperations.ImportBankSegmentsAsync(m_entryTyped, files.Select((IStorageFile file) => file.Path.LocalPath)).ConfigureAwait(continueOnCapturedContext: true);
		StatusText = result.Message;
		if (result.Success)
		{
			ReloadFromSource();
		}
	}
	private async Task ExportDataSet()
	{
		StatusText = (await ExportDataSetCoreAsync().ConfigureAwait(continueOnCapturedContext: true)).Message;
	}
	private async Task ImportDataSet()
	{
		StatusText = (await ImportDataSetCoreAsync().ConfigureAwait(continueOnCapturedContext: true)).Message;
	}
	private async Task Save()
	{
		StatusText = (await SaveCoreAsync().ConfigureAwait(continueOnCapturedContext: true)).Message;
	}
	private async Task Add()
	{
		if (IsDataSetsMode)
		{
			SoundOperationResult result = AddDataSetRowCore();
			StatusText = result.Message;
			return;
		}
		SoundOperationResult saveResult = await EnsureSavedBeforeImmediateCommitAsync().ConfigureAwait(continueOnCapturedContext: true);
		if (!saveResult.Success)
		{
			StatusText = saveResult.Message;
			return;
		}
		SoundOperationResult addResult = await SoundAssetOperations.AddWithPickerAsync(m_entryTyped).ConfigureAwait(continueOnCapturedContext: true);
		StatusText = addResult.Message;
		if (addResult.Success)
		{
			ReloadFromSource();
		}
	}
	private void Remove()
	{
		SoundOperationResult soundOperationResult = RemoveDataSetRowCore();
		StatusText = soundOperationResult.Message;
	}
	private async Task Revert()
	{
		StopPlaybackInternal(resetState: true);
		if (HasPendingDataSetEdits && !IsModified)
		{
			ReloadFromSource();
			StatusText = "Discarded pending data-set edits.";
			return;
		}
		SoundOperationResult result = await SoundAssetOperations.RevertAsync(m_entryTyped).ConfigureAwait(continueOnCapturedContext: true);
		StatusText = result.Message;
		if (result.Success)
		{
			ReloadFromSource();
		}
	}

	private async Task<SoundOperationResult> SaveCoreAsync()
	{
		if ((object)m_state == null)
		{
			return new SoundOperationResult(Success: false, "Sound state is not loaded.");
		}
		if (!HasPendingDataSetEdits)
		{
			return new SoundOperationResult(Success: false, "There are no pending data-set edits to save.");
		}
		if (!ValidateAllDataSetRows())
		{
			return new SoundOperationResult(Success: false, "Fix the data-set validation errors before saving.");
		}
		SoundOperationResult result = SoundAssetOperations.Save(m_state);
		if (result.Success)
		{
			ReloadFromSource();
		}
		return result;
	}

	private async Task<SoundOperationResult> ExportSelectionCoreAsync()
	{
		if (SelectedSegment == null)
		{
			return new SoundOperationResult(Success: false, "Select a sound segment to export.");
		}
		return await ExportSegmentCoreAsync(SelectedSegment).ConfigureAwait(continueOnCapturedContext: true);
	}

	private async Task<SoundOperationResult> ExportSegmentCoreAsync(SoundSegmentItemModel? segment)
	{
		if (segment == null)
		{
			return new SoundOperationResult(Success: false, "Select a sound segment to export.");
		}
		if ((object)m_state == null)
		{
			return new SoundOperationResult(Success: false, "Sound state is not loaded.");
		}
		try
		{
			if (m_state.Kind == SoundAssetKind.Bank && m_state.ParsedBank != null)
			{
				return await SoundAssetOperations.ExportSegmentWithPickerAsync(m_entryTyped, segment.VariationListIndex, segment.SegmentListIndex).ConfigureAwait(continueOnCapturedContext: true);
			}
			return await SoundAssetOperations.ExportWithPickerAsync(m_entryTyped).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception ex)
		{
			return new SoundOperationResult(Success: false, "Segment export failed: " + ex.Message);
		}
	}

	private async Task<SoundOperationResult> ImportSegmentCoreAsync(SoundSegmentItemModel? segment)
	{
		if (segment == null)
		{
			return new SoundOperationResult(Success: false, "Select a sound segment to import.");
		}
		SoundOperationResult saveResult = await EnsureSavedBeforeImmediateCommitAsync().ConfigureAwait(continueOnCapturedContext: true);
		if (!saveResult.Success)
		{
			return saveResult;
		}
		FilePickerOpenOptions filePickerOpenOptions = new FilePickerOpenOptions();
		filePickerOpenOptions.Title = "Import sound segment";
		filePickerOpenOptions.AllowMultiple = false;
		filePickerOpenOptions.FileTypeFilter = new _003C_003Ez__ReadOnlySingleElementList<FilePickerFileType>(new FilePickerFileType("Audio files")
		{
			Patterns = new _003C_003Ez__ReadOnlyArray<string>(new string[6] { "*.sps", "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a" })
		});
		IStorageFile file = (await FileService.OpenFilesAsync(filePickerOpenOptions).ConfigureAwait(continueOnCapturedContext: true))?.FirstOrDefault();
		if (file == null)
		{
			return new SoundOperationResult(Success: false, "Sound import canceled.");
		}
		SoundAssetState? state = m_state;
		SoundOperationResult result = (((object)state == null || state.Kind != SoundAssetKind.Bank || m_state.ParsedBank == null) ? (await SoundAssetOperations.ImportWithPickerAsync(m_entryTyped).ConfigureAwait(continueOnCapturedContext: true)) : (await SoundAssetOperations.ImportSegmentAsync(m_entryTyped, segment.VariationListIndex, segment.SegmentListIndex, file.Path.LocalPath).ConfigureAwait(continueOnCapturedContext: true)));
		if (result.Success)
		{
			ReloadFromSource();
		}
		return result;
	}

	private async Task<SoundOperationResult> EnsureSavedBeforeImmediateCommitAsync()
	{
		if (!HasPendingDataSetEdits)
		{
			return new SoundOperationResult(Success: true, "No staged data-set changes.");
		}
		SoundOperationResult result = await SaveCoreAsync().ConfigureAwait(continueOnCapturedContext: true);
		return result.Success ? new SoundOperationResult(Success: true, "Saved staged data-set edits.") : result;
	}

	private SoundOperationResult AddDataSetRowCore()
	{
		SoundAssetState? state = m_state;
		if ((object)state == null || state.Kind != SoundAssetKind.Bank || m_state.ParsedBank == null || SelectedDataSet == null)
		{
			return new SoundOperationResult(Success: false, "Select a data set before adding a row.");
		}
		int value = SoundAssetOperations.AddDataSetRow(m_state, SelectedDataSet.Id);
		HasPendingDataSetEdits = true;
		RebuildDataSetViews(SelectedDataSet.Id, value);
		return new SoundOperationResult(Success: true, $"Added row {value} to {SelectedDataSet.DisplayName}.");
	}

	private SoundOperationResult RemoveDataSetRowCore()
	{
		SoundAssetState? state = m_state;
		if ((object)state == null || state.Kind != SoundAssetKind.Bank || m_state.ParsedBank == null || SelectedDataSet == null || SelectedDataSetRow == null)
		{
			return new SoundOperationResult(Success: false, "Select a data-set row to remove.");
		}
		int index = SelectedDataSetRow.Index;
		SoundAssetOperations.RemoveDataSetRow(m_state, SelectedDataSet.Id, index);
		HasPendingDataSetEdits = true;
		RebuildDataSetViews(SelectedDataSet.Id, Math.Max(0, index - 1));
		return new SoundOperationResult(Success: true, $"Removed row {index} from {SelectedDataSet.DisplayName}.");
	}

	private async Task<SoundOperationResult> ExportDataSetCoreAsync()
	{
		if (SelectedDataSet == null)
		{
			return new SoundOperationResult(Success: false, "Select a data set to export.");
		}
		FilePickerSaveOptions filePickerSaveOptions = new FilePickerSaveOptions();
		filePickerSaveOptions.Title = "Export data set";
		filePickerSaveOptions.SuggestedFileName = SafeName(m_entryTyped.Filename) + "_" + SafeName(SelectedDataSet.DisplayName);
		filePickerSaveOptions.DefaultExtension = "xlsx";
		filePickerSaveOptions.FileTypeChoices = new _003C_003Ez__ReadOnlyArray<FilePickerFileType>(new FilePickerFileType[2]
		{
			new FilePickerFileType("Excel Workbook (*.xlsx)")
			{
				Patterns = new _003C_003Ez__ReadOnlySingleElementList<string>("*.xlsx")
			},
			new FilePickerFileType("CSV (*.csv)")
			{
				Patterns = new _003C_003Ez__ReadOnlySingleElementList<string>("*.csv")
			}
		});
		IStorageFile file = await FileService.SaveFilePickerAsync(filePickerSaveOptions).ConfigureAwait(continueOnCapturedContext: true);
		if (file == null)
		{
			return new SoundOperationResult(Success: false, "Data-set export canceled.");
		}
		string path = file.Path.LocalPath;
		try
		{
			EnsureDataSetLoaded(SelectedDataSet);
			if (System.IO.Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
			{
				await File.WriteAllTextAsync(path, BuildCsv(SelectedDataSet), Encoding.UTF8).ConfigureAwait(continueOnCapturedContext: true);
			}
			else
			{
				using XLWorkbook workbook = new XLWorkbook();
				IXLWorksheet worksheet = workbook.Worksheets.Add(SanitizeWorksheetName(SelectedDataSet.DisplayName));
				WriteWorksheet(worksheet, SelectedDataSet);
				workbook.SaveAs(path);
			}
			return new SoundOperationResult(Success: true, $"Exported {SelectedDataSet.DisplayName} to '{path}'.");
		}
		catch (Exception ex)
		{
			return new SoundOperationResult(Success: false, "Data-set export failed: " + ex.Message);
		}
	}

	private async Task<SoundOperationResult> ImportDataSetCoreAsync()
	{
		int num;
		if (SelectedDataSet != null)
		{
			SoundAssetState? state = m_state;
			if ((object)state != null && state.Kind == SoundAssetKind.Bank)
			{
				num = ((m_state.ParsedBank == null) ? 1 : 0);
				goto IL_0054;
			}
		}
		num = 1;
		goto IL_0054;
		IL_0054:
		if (num != 0)
		{
			return new SoundOperationResult(Success: false, "Select a data set to import.");
		}
		FilePickerOpenOptions filePickerOpenOptions = new FilePickerOpenOptions();
		filePickerOpenOptions.Title = "Import data set";
		filePickerOpenOptions.AllowMultiple = false;
		filePickerOpenOptions.FileTypeFilter = new _003C_003Ez__ReadOnlySingleElementList<FilePickerFileType>(new FilePickerFileType("CSV and Excel files")
		{
			Patterns = new _003C_003Ez__ReadOnlyArray<string>(new string[2] { "*.csv", "*.xlsx" })
		});
		IStorageFile file = (await FileService.OpenFilesAsync(filePickerOpenOptions).ConfigureAwait(continueOnCapturedContext: true))?.FirstOrDefault();
		if (file == null)
		{
			return new SoundOperationResult(Success: false, "Data-set import canceled.");
		}
		try
		{
			EnsureDataSetLoaded(SelectedDataSet);
			IReadOnlyList<ImportedDataSetRow> importedRows = (System.IO.Path.GetExtension(file.Path.LocalPath).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ParseCsvDataSet(file.Path.LocalPath, SelectedDataSet) : ParseWorksheetDataSet(file.Path.LocalPath, SelectedDataSet));
			ApplyImportedDataSet(SelectedDataSet, importedRows);
			return new SoundOperationResult(Success: true, $"Imported {importedRows.Count:N0} row(s) into {SelectedDataSet.DisplayName}.");
		}
		catch (Exception ex)
		{
			return new SoundOperationResult(Success: false, "Data-set import failed: " + ex.Message);
		}
	}

	private async Task PlaySegmentCoreAsync(SoundSegmentItemModel segment)
	{
		if (segment == m_playingSegment && IsPlaybackActive)
		{
			if (segment.IsPaused)
			{
				ResumePlayback();
			}
			else
			{
				PausePlayback();
			}
			return;
		}
		SelectedVariation = Variations.FirstOrDefault((SoundVariationItemModel variation) => variation.ListIndex == segment.VariationListIndex) ?? SelectedVariation;
		SelectedSegment = segment;
		StopPlaybackInternal(resetState: true);
		if ((object)m_state == null)
		{
			StatusText = "Sound state is not loaded.";
			return;
		}
		try
		{
			RequestExclusivePlaybackOwnership();
			string previewPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
			await ExportPreviewAsync(segment, previewPath).ConfigureAwait(continueOnCapturedContext: true);
			m_audioReader = new AudioFileReader(previewPath);
			m_waveOut = CreatePlaybackDevice(m_audioReader);
			m_waveOut.PlaybackStopped += OnPlaybackStopped;
			m_previewPath = previewPath;
			m_playingSegment = segment;
			m_playingVariation = SelectedVariation;
			m_stopRequested = false;
			foreach (SoundSegmentItemModel item in SegmentRows)
			{
				if (item != segment)
				{
					item.ResetPlaybackState();
				}
			}
			segment.IsCurrent = true;
			segment.IsPlaying = true;
			segment.IsPaused = false;
			segment.PlaybackState = SoundPlaybackState.Playing;
			segment.PlaybackStateText = "Playing";
			segment.ProgressMaximum = Math.Max(0.001, m_audioReader.TotalTime.TotalSeconds);
			PlaybackMaximum = segment.ProgressMaximum;
			PlaybackPosition = 0.0;
			PlaybackTargetText = segment.Name + " (" + (SelectedVariation?.Name ?? "Sound") + ")";
			PlaybackStateText = "Playing";
			UpdatePlaybackTimeline(TimeSpan.Zero, m_audioReader.TotalTime);
			UpdateVariationPlaybackSummary();
			m_waveOut.Play();
			IsPlaybackActive = true;
			m_playbackTimer.Start();
			RaiseComputedPropertyChanges();
		}
		catch (Exception ex)
		{
			StopPlaybackInternal(resetState: true);
			segment.PlaybackState = SoundPlaybackState.Error;
			segment.PlaybackStateText = "Error";
			StatusText = "Playback failed: " + ex.Message;
		}
	}

	private async Task ExportPreviewAsync(SoundSegmentItemModel segment, string outputPath)
	{
		if ((object)m_state == null)
		{
			throw new InvalidOperationException("Sound state is not loaded.");
		}
		if (m_state.Kind == SoundAssetKind.Bank && m_state.ParsedBank != null)
		{
			await SoundAssetOperations.ExportSegmentAsync(m_state, segment.VariationListIndex, segment.SegmentListIndex, outputPath).ConfigureAwait(continueOnCapturedContext: true);
		}
		else
		{
			await SoundAssetOperations.ExportChunkAsync(m_state, segment.SegmentListIndex, outputPath).ConfigureAwait(continueOnCapturedContext: true);
		}
	}

	private void UpdatePlayback()
	{
		if (m_audioReader != null && m_playingSegment != null)
		{
			TimeSpan currentTime = m_audioReader.CurrentTime;
			TimeSpan totalTime = m_audioReader.TotalTime;
			m_isSeeking = true;
			try
			{
				PlaybackMaximum = Math.Max(0.001, totalTime.TotalSeconds);
				PlaybackPosition = Math.Clamp(currentTime.TotalSeconds, 0.0, PlaybackMaximum);
			}
			finally
			{
				m_isSeeking = false;
			}
			m_playingSegment.ProgressMaximum = PlaybackMaximum;
			m_playingSegment.ProgressValue = PlaybackPosition;
			m_playingSegment.ElapsedText = FormatTimeline(currentTime);
			UpdatePlaybackTimeline(currentTime, totalTime);
		}
	}

	private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
	{
		Dispatcher.UIThread.Post(delegate
		{
			m_playbackTimer.Stop();
			if (m_playingSegment != null)
			{
				bool flag = !m_stopRequested && e.Exception == null && m_audioReader != null && m_audioReader.CurrentTime >= m_audioReader.TotalTime - TimeSpan.FromMilliseconds(75.0);
				m_playingSegment.IsPlaying = false;
				m_playingSegment.IsPaused = false;
				m_playingSegment.IsCurrent = false;
				m_playingSegment.PlaybackState = (flag ? SoundPlaybackState.Completed : SoundPlaybackState.Stopped);
				m_playingSegment.PlaybackStateText = (flag ? "Completed" : "Stopped");
				m_playingSegment.ProgressValue = (flag ? m_playingSegment.ProgressMaximum : 0.0);
				m_playingSegment.ElapsedText = (flag ? FormatTimeline(TimeSpan.FromSeconds(m_playingSegment.ProgressMaximum)) : "0:00.000");
			}
			PlaybackStateText = ((e.Exception != null) ? "Error" : (m_stopRequested ? "Stopped" : "Completed"));
			IsPlaybackActive = false;
			UpdateVariationPlaybackSummary();
			RaiseComputedPropertyChanges();
			if (e.Exception != null)
			{
				StatusText = "Playback failed: " + e.Exception.Message;
			}
			DisposePlaybackObjects(deletePreview: true);
		}, DispatcherPriority.Normal);
	}

	private void StopPlaybackInternal(bool resetState)
	{
		m_stopRequested = true;
		m_playbackTimer.Stop();
		if (m_waveOut != null)
		{
			try
			{
				m_waveOut.Stop();
			}
			catch
			{
			}
		}
		if (resetState)
		{
			if (m_playingSegment != null)
			{
				m_playingSegment.ResetPlaybackState();
			}
			PlaybackStateText = "Idle";
			PlaybackTargetText = "No preview active.";
			UpdatePlaybackTimeline(TimeSpan.Zero, TimeSpan.Zero);
			IsPlaybackActive = false;
			UpdateVariationPlaybackSummary();
			RaiseComputedPropertyChanges();
		}
		DisposePlaybackObjects(deletePreview: true);
	}

	private void DisposePlaybackObjects(bool deletePreview)
	{
		if (m_waveOut != null)
		{
			m_waveOut.PlaybackStopped -= OnPlaybackStopped;
			m_waveOut.Dispose();
			m_waveOut = null;
		}
		m_audioReader?.Dispose();
		m_audioReader = null;
		if (deletePreview && !string.IsNullOrWhiteSpace(m_previewPath))
		{
			try
			{
				File.Delete(m_previewPath);
			}
			catch
			{
			}
			m_previewPath = null;
		}
		m_playingSegment = null;
		m_playingVariation = null;
		m_stopRequested = false;
		ReleaseExclusivePlaybackOwnership();
	}

	private void RequestExclusivePlaybackOwnership()
	{
		lock (s_playbackOwnershipLock)
		{
			if (s_activePlaybackOwner != null && s_activePlaybackOwner.TryGetTarget(out SoundAssetEditorViewModel target) && target != this)
			{
				target.StopPlaybackForExclusiveSwitch();
			}
			s_activePlaybackOwner = new WeakReference<SoundAssetEditorViewModel>(this);
		}
	}

	private void ReleaseExclusivePlaybackOwnership()
	{
		lock (s_playbackOwnershipLock)
		{
			if (s_activePlaybackOwner != null && s_activePlaybackOwner.TryGetTarget(out SoundAssetEditorViewModel target) && target == this)
			{
				s_activePlaybackOwner = null;
			}
		}
	}

	private void StopPlaybackForExclusiveSwitch()
	{
		if (!IsPlaybackActive)
		{
			IWavePlayer? waveOut = m_waveOut;
			if (waveOut == null || waveOut.PlaybackState != PlaybackState.Paused)
			{
				return;
			}
		}
		StopPlaybackInternal(resetState: true);
		StatusText = "Playback stopped because another sound started.";
	}

	private static IWavePlayer CreatePlaybackDevice(WaveStream reader)
	{
		try
		{
			MMDevice defaultAudioEndpoint = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
			WasapiOut wasapiOut = new WasapiOut(defaultAudioEndpoint, AudioClientShareMode.Shared, useEventSync: false, 100);
			wasapiOut.Init(CreatePlaybackProvider(reader, defaultAudioEndpoint.AudioClient.MixFormat.SampleRate));
			return wasapiOut;
		}
		catch
		{
			reader.Position = 0L;
			WaveOutEvent waveOutEvent = new WaveOutEvent();
			waveOutEvent.Init(CreatePlaybackProvider(reader));
			return waveOutEvent;
		}
	}

	private static IWaveProvider CreatePlaybackProvider(WaveStream reader, int? targetSampleRate = null)
	{
		if (!(reader is ISampleProvider sampleProvider))
		{
			throw new InvalidOperationException("Preview stream does not expose sample data.");
		}
		int channels = sampleProvider.WaveFormat.Channels;
		if (1 == 0)
		{
		}
		ISampleProvider sampleProvider2 = channels switch
		{
			1 => new MonoToStereoSampleProvider(sampleProvider), 
			2 => sampleProvider, 
			_ => new AveragedStereoSampleProvider(sampleProvider), 
		};
		if (1 == 0)
		{
		}
		ISampleProvider sampleProvider3 = sampleProvider2;
		if (targetSampleRate.HasValue && targetSampleRate.GetValueOrDefault() > 0 && sampleProvider3.WaveFormat.SampleRate != targetSampleRate.Value)
		{
			sampleProvider3 = new WdlResamplingSampleProvider(sampleProvider3, targetSampleRate.Value);
		}
		return new SampleToWaveProvider16(sampleProvider3);
	}

	private SoundEditorLoadSnapshot BuildLoadSnapshot(SoundAssetState state)
	{
		List<SoundVariationItemModel> list = new List<SoundVariationItemModel>();
		List<SoundSegmentItemModel> list2 = new List<SoundSegmentItemModel>();
		List<SoundDatasetSheetModel> list3 = new List<SoundDatasetSheetModel>();
		if (state.ParsedBank != null)
		{
			foreach (DataSet item in state.ParsedBank.AllDataSets.OrderBy<DataSet, string>((DataSet ds) => ResolveHashName(ds.Id), StringComparer.OrdinalIgnoreCase).ThenBy((DataSet ds) => ds.Id))
			{
				list3.Add(BuildDataSetModel(item));
			}
		}
		string bankSummaryText;
		string operationsText;
		string capabilitySummaryText;
		if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
		{
			for (int num = 0; num < state.ParsedBank.Variations.Count; num++)
			{
				SoundVariationItemModel soundVariationItemModel = BuildVariationModel(num, state.ParsedBank.Variations[num]);
				list.Add(soundVariationItemModel);
				list2.AddRange(soundVariationItemModel.Segments);
			}
			int value = state.ParsedBank.Variations.Sum((Variation variation) => variation.Segments.Count);
			bankSummaryText = $"{state.ParsedBank.Variations.Count:N0} variation(s), {value:N0} segment(s), {list3.Count:N0} data set(s)";
			operationsText = "Preview, per-segment import/export, bulk segment import/export, add sound, and data-set editing are available.";
			capabilitySummaryText = "Bulk import accepts numeric filenames and grows the bank one contiguous segment at a time. Data sets support CSV/XLSX import/export and staged save.";
		}
		else if (state.Chunks.Count > 0)
		{
			SoundVariationItemModel soundVariationItemModel2 = BuildChunkVariationModel(state.Chunks);
			list.Add(soundVariationItemModel2);
			list2.AddRange(soundVariationItemModel2.Segments);
			bankSummaryText = ((list3.Count > 0) ? $"{state.Chunks.Count:N0} raw sound chunk(s), {list3.Count:N0} data set(s)" : $"{state.Chunks.Count:N0} raw sound chunk(s)");
			operationsText = "Preview, export, and replace are available for the resolved sound chunks.";
			capabilitySummaryText = ((list3.Count > 0) ? "Preview and chunk-level replace are available for the resolved sound chunks. Data sets are available through the RES-backed bank." : "This asset does not expose an editable sound bank or data sets, so only chunk-level operations are available.");
		}
		else
		{
			bankSummaryText = "No sound chunks were resolved.";
			operationsText = "No operations are available for this asset.";
			capabilitySummaryText = "This asset does not expose any supported sound payloads.";
		}
		return new SoundEditorLoadSnapshot
		{
			State = state,
			SourceText = BuildSourceSummary(state),
			BankSummaryText = bankSummaryText,
			OperationsText = operationsText,
			CapabilitySummaryText = capabilitySummaryText,
			Variations = list,
			SegmentRows = list2,
			DataSets = list3,
			IsModified = IsModifiedFromState(state)
		};
	}

	private void ApplyLoadSnapshot(SoundEditorLoadSnapshot snapshot, int? selectedVariationIndex, int? selectedSegmentIndex, string? selectedDataSetId, int? selectedRowIndex, SoundEditorMode currentMode)
	{
		m_state = snapshot.State;
		Variations.Clear();
		SegmentRows.Clear();
		SelectedVariationSegments.Clear();
		DataSets.Clear();
		SelectedDataSetColumns.Clear();
		SelectedDataSetRows.Clear();
		SourceText = snapshot.SourceText;
		BankSummaryText = snapshot.BankSummaryText;
		OperationsText = snapshot.OperationsText;
		CapabilitySummaryText = snapshot.CapabilitySummaryText;
		foreach (SoundVariationItemModel variation in snapshot.Variations)
		{
			Variations.Add(variation);
		}
		foreach (SoundSegmentItemModel segmentRow in snapshot.SegmentRows)
		{
			segmentRow.PropertyChanged += OnSegmentItemPropertyChanged;
			SegmentRows.Add(segmentRow);
		}
		foreach (SoundDatasetSheetModel dataSet in snapshot.DataSets)
		{
			DataSets.Add(dataSet);
		}
		SelectedVariation = (selectedVariationIndex.HasValue ? Variations.FirstOrDefault((SoundVariationItemModel variation) => variation.ListIndex == selectedVariationIndex.Value) : Variations.FirstOrDefault());
		if (SelectedVariation != null)
		{
			SelectedSegment = (selectedSegmentIndex.HasValue ? SelectedVariation.Segments.FirstOrDefault((SoundSegmentItemModel segment) => segment.SegmentListIndex == selectedSegmentIndex.Value) : SelectedVariation.Segments.FirstOrDefault());
		}
		if (SelectedSegment == null)
		{
			SelectedSegment = (selectedSegmentIndex.HasValue ? SegmentRows.FirstOrDefault((SoundSegmentItemModel segment) => segment.SegmentListIndex == selectedSegmentIndex.Value) : SegmentRows.FirstOrDefault());
		}
		SelectedDataSet = ResolveInitialDataSetSelection(selectedDataSetId);
		if (SelectedDataSet != null)
		{
			EnsureDataSetLoaded(SelectedDataSet);
			SelectedDataSetRow = (selectedRowIndex.HasValue ? SelectedDataSet.Rows.FirstOrDefault((SoundDatasetRowModel row) => row.Index == selectedRowIndex.Value) : SelectedDataSet.Rows.FirstOrDefault());
		}
		Mode = ((DataSets.Count != 0) ? currentMode : SoundEditorMode.Variations);
		HasPendingDataSetEdits = false;
		IsModified = snapshot.IsModified;
		StatusText = "Sound editor ready.";
		RefreshSelectionSummary();
		RaiseComputedPropertyChanges();
	}

	private void OnSegmentItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "IsSelected")
		{
			OnPropertyChanged("AllSegmentsSelected");
		}
	}

	private SoundVariationItemModel BuildVariationModel(int listIndex, Variation variation)
	{
		SoundVariationItemModel soundVariationItemModel = new SoundVariationItemModel
		{
			SourceObject = variation,
			ListIndex = listIndex,
			Index = variation.Index,
			VariationId = variation.VariationId,
			Name = $"Variation {variation.Index:0000}",
			SelectionText = "VariationId " + ResolveHashName(variation.VariationId),
			SegmentCountText = $"{variation.Segments.Count:N0} segment(s)",
			ChunkText = $"Chunk {variation.ChunkRef.ChunkIndex} | {variation.ChunkRef.ChunkId}",
			PlaybackSummaryText = "Ready"
		};
		double num = 0.0;
		for (int i = 0; i < variation.Segments.Count; i++)
		{
			SoundSegmentItemModel item = BuildSegmentModel(listIndex, i, variation, variation.Segments[i]);
			soundVariationItemModel.Segments.Add(item);
			num += (double)Math.Max(0f, variation.Segments[i].SegmentLength);
		}
		soundVariationItemModel.DurationText = FormatSeconds(num);
		soundVariationItemModel.SampleRateText = "Sample rate pending";
		soundVariationItemModel.CodecText = "Codec pending";
		return soundVariationItemModel;
	}

	private SoundVariationItemModel BuildChunkVariationModel(IReadOnlyList<SoundChunkBinding> chunks)
	{
		SoundVariationItemModel soundVariationItemModel = new SoundVariationItemModel
		{
			Name = "Resolved Chunks",
			SelectionText = "Chunk-backed sound asset",
			DurationText = "Unknown duration",
			CodecText = "Chunk payload",
			SegmentCountText = $"{chunks.Count:N0} chunk(s)",
			ChunkText = "Raw chunks",
			SampleRateText = "Sample rate unknown",
			PlaybackSummaryText = "Ready"
		};
		for (int i = 0; i < chunks.Count; i++)
		{
			SoundChunkBinding soundChunkBinding = chunks[i];
			soundVariationItemModel.Segments.Add(new SoundSegmentItemModel
			{
				SourceObject = soundChunkBinding,
				VariationListIndex = 0,
				SegmentListIndex = i,
				Index = soundChunkBinding.Index,
				VariationId = 0u,
				Name = soundChunkBinding.Name,
				SegmentDisplayText = soundChunkBinding.Index.ToString(CultureInfo.InvariantCulture),
				VariationDisplayText = i.ToString(CultureInfo.InvariantCulture),
				CodecText = "Chunk payload",
				DurationText = "Unknown duration",
				ChannelText = "Channels unknown",
				SampleRateText = "Sample rate pending",
				FlagsText = $"{soundChunkBinding.ChunkSize:N0} bytes | {soundChunkBinding.ChunkId}",
				DurationSeconds = 0.0,
				ProgressMaximum = 1.0
			});
		}
		return soundVariationItemModel;
	}

	private SoundSegmentItemModel BuildSegmentModel(int variationListIndex, int segmentListIndex, Variation variation, Segment segment)
	{
		double num = Math.Max(0f, segment.SegmentLength);
		int num2 = 1;
		List<string> list = new List<string>(num2);
		CollectionsMarshal.SetCount(list, num2);
		CollectionsMarshal.AsSpan(list)[0] = (((segment.SeekTableOffset & 1) != 0) ? "Seekable" : "No seek table");
		List<string> values = list;
		return new SoundSegmentItemModel
		{
			SourceObject = segment,
			VariationListIndex = variationListIndex,
			SegmentListIndex = segmentListIndex,
			Index = segment.Index,
			VariationId = variation.VariationId,
			Name = $"Segment {segment.Index:0000}",
			SegmentDisplayText = segment.Index.ToString(CultureInfo.InvariantCulture),
			VariationDisplayText = variation.VariationId.ToString(CultureInfo.InvariantCulture),
			CodecText = "Codec pending",
			DurationText = FormatSeconds(num),
			ChannelText = "Config pending",
			SampleRateText = "Sample rate pending",
			FlagsText = string.Join(" | ", values),
			DurationSeconds = num,
			ProgressMaximum = ((num > 0.0) ? num : 1.0)
		};
	}

	private SoundDatasetSheetModel BuildDataSetModel(DataSet dataSet)
	{
		SoundDatasetSheetModel soundDatasetSheetModel = new SoundDatasetSheetModel
		{
			SourceObject = dataSet,
			Id = ResolveIdentifier(dataSet.Id),
			DisplayName = ResolveHashName(dataSet.Id),
			CanAddRow = true,
			IsLoaded = false
		};
		List<Field> list = dataSet.IndexColumns.Concat(dataSet.Fields).ToList();
		for (int i = 0; i < list.Count; i++)
		{
			Field field = list[i];
			soundDatasetSheetModel.Columns.Add(new SoundDatasetColumnModel
			{
				SourceObject = field,
				Key = ResolveIdentifier(field.Id),
				Header = ResolveHashName(field.Id),
				FieldType = field.DataType,
				CellIndex = i,
				IsEditable = true,
				Width = ((field.DataType == FieldType.String) ? 240 : 180)
			});
		}
		int value = (soundDatasetSheetModel.RowCount = GetRowCount(dataSet));
		soundDatasetSheetModel.RowCountText = $"{value:N0} row(s)";
		UpdateDataSetState(soundDatasetSheetModel);
		return soundDatasetSheetModel;
	}

	private void EnsureDataSetLoaded(SoundDatasetSheetModel? sheet)
	{
		if (sheet == null || sheet.IsLoaded || !(sheet.SourceObject is DataSet dataSet))
		{
			return;
		}
		int rowCount = GetRowCount(dataSet);
		for (int i = 0; i < rowCount; i++)
		{
			SoundDatasetRowModel row = new SoundDatasetRowModel
			{
				SourceObject = dataSet,
				Index = i
			};
			foreach (SoundDatasetColumnModel column in sheet.Columns)
			{
				Field field = (Field)column.SourceObject;
				object value = ((i < field.Values.Count) ? field.Values[i] : null);
				SoundDatasetCellModel cell = new SoundDatasetCellModel
				{
					SourceObject = field,
					Key = column.Key,
					Header = column.Header,
					Width = column.Width,
					RowIndex = i,
					IsEditable = column.IsEditable,
					FieldType = column.FieldType
				};
				cell.SetCommitCallback(delegate(object value2)
				{
					CommitDataSetCell(sheet, row, cell, value2);
				});
				cell.SetCommittedValue(value, notify: false);
				cell.PropertyChanged += delegate(object? _, PropertyChangedEventArgs e)
				{
					OnDataSetCellPropertyChanged(sheet, row, cell, e.PropertyName);
				};
				row.Cells.Add(cell);
			}
			row.Name = BuildRowName(row);
			UpdateRowState(row);
			sheet.Rows.Add(row);
		}
		sheet.IsLoaded = true;
		UpdateDataSetState(sheet);
	}

	private void CommitDataSetCell(SoundDatasetSheetModel sheet, SoundDatasetRowModel row, SoundDatasetCellModel cell, object value)
	{
		if (m_state?.ParsedBank != null)
		{
			SoundAssetOperations.SetDataSetValue(m_state, sheet.Id, row.Index, cell.Key, value);
			HasPendingDataSetEdits = true;
			UpdateRowState(row);
			UpdateDataSetState(sheet);
			UpdateDataSetEditorSummary();
			StatusText = "Staged changes for " + sheet.DisplayName + ".";
		}
	}

	private void OnDataSetCellPropertyChanged(SoundDatasetSheetModel sheet, SoundDatasetRowModel row, SoundDatasetCellModel cell, string? propertyName)
	{
		switch (propertyName)
		{
		default:
			if (!(propertyName == "BooleanValue"))
			{
				return;
			}
			break;
		case "IsDirty":
		case "HasValidationError":
		case "ValidationError":
		case "EditorValue":
			break;
		}
		row.Name = BuildRowName(row);
		row.RefreshValidationState();
		UpdateRowState(row);
		UpdateDataSetState(sheet);
		UpdateDataSetEditorSummary();
		if (SelectedDataSetRow == row)
		{
			SelectedDataSetRowSummaryText = row.Name + " | " + row.StateText;
		}
		RaiseComputedPropertyChanges();
	}

	private void RebuildDataSetViews(string dataSetId, int? rowIndex = null)
	{
		if ((object)m_state != null && m_state.ParsedBank != null)
		{
			ApplyLoadSnapshot(BuildLoadSnapshot(m_state), SelectedVariation?.ListIndex, SelectedSegment?.SegmentListIndex, dataSetId, rowIndex, Mode);
			HasPendingDataSetEdits = true;
			RaiseComputedPropertyChanges();
		}
	}

	private void ApplyImportedDataSet(SoundDatasetSheetModel sheet, IReadOnlyList<ImportedDataSetRow> importedRows)
	{
		if (m_state?.ParsedBank == null || !(sheet.SourceObject is DataSet dataSet))
		{
			return;
		}
		int i;
		for (i = GetRowCount(dataSet); i < importedRows.Count; i++)
		{
			SoundAssetOperations.AddDataSetRow(m_state, sheet.Id);
		}
		while (i > importedRows.Count)
		{
			SoundAssetOperations.RemoveDataSetRow(m_state, sheet.Id, i - 1);
			i--;
		}
		for (int j = 0; j < importedRows.Count; j++)
		{
			foreach (var (fieldIdentifier, value) in importedRows[j].Values)
			{
				SoundAssetOperations.SetDataSetValue(m_state, sheet.Id, j, fieldIdentifier, value);
			}
		}
		HasPendingDataSetEdits = true;
		RebuildDataSetViews(sheet.Id, (importedRows.Count == 0) ? ((int?)null) : new int?(0));
	}

	private IReadOnlyList<ImportedDataSetRow> ParseCsvDataSet(string path, SoundDatasetSheetModel sheet)
	{
		string input = File.ReadAllText(path);
		char delimiter = DetectCsvDelimiter(input);
		List<IReadOnlyList<string>> list = ParseDelimitedRows(input, delimiter);
		if (list.Count == 0)
		{
			return Array.Empty<ImportedDataSetRow>();
		}
		return ConvertImportedRows(sheet, list[0], list.Skip(1).ToList());
	}

	private IReadOnlyList<ImportedDataSetRow> ParseWorksheetDataSet(string path, SoundDatasetSheetModel sheet)
	{
		using XLWorkbook xLWorkbook = new XLWorkbook(path);
		IXLWorksheet iXLWorksheet = xLWorkbook.Worksheets.First();
		IXLRange iXLRange = iXLWorksheet.RangeUsed();
		if (iXLRange == null)
		{
			return Array.Empty<ImportedDataSetRow>();
		}
		List<string> list = new List<string>();
		int rowNumber = iXLRange.RangeAddress.FirstAddress.RowNumber;
		int columnNumber = iXLRange.RangeAddress.FirstAddress.ColumnNumber;
		int rowNumber2 = iXLRange.RangeAddress.LastAddress.RowNumber;
		int columnNumber2 = iXLRange.RangeAddress.LastAddress.ColumnNumber;
		for (int i = columnNumber; i <= columnNumber2; i++)
		{
			list.Add(iXLWorksheet.Cell(rowNumber, i).GetValue<string>());
		}
		List<IReadOnlyList<string>> list2 = new List<IReadOnlyList<string>>();
		for (int j = rowNumber + 1; j <= rowNumber2; j++)
		{
			List<string> list3 = new List<string>();
			for (int k = columnNumber; k <= columnNumber2; k++)
			{
				list3.Add(iXLWorksheet.Cell(j, k).GetValue<string>());
			}
			list2.Add(list3);
		}
		return ConvertImportedRows(sheet, list, list2);
	}

	private IReadOnlyList<ImportedDataSetRow> ConvertImportedRows(SoundDatasetSheetModel sheet, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
	{
		Dictionary<int, SoundDatasetColumnModel> dictionary = new Dictionary<int, SoundDatasetColumnModel>();
		for (int i = 0; i < headers.Count; i++)
		{
			string normalized = NormalizeHeader(headers[i]);
			SoundDatasetColumnModel soundDatasetColumnModel = sheet.Columns.FirstOrDefault((SoundDatasetColumnModel candidate) => NormalizeHeader(candidate.Header) == normalized || NormalizeHeader(candidate.Key) == normalized);
			if (soundDatasetColumnModel != null)
			{
				dictionary[i] = soundDatasetColumnModel;
			}
		}
		if (dictionary.Count == 0)
		{
			throw new InvalidDataException("The selected file does not contain any matching data-set columns.");
		}
		List<ImportedDataSetRow> list = new List<ImportedDataSetRow>();
		foreach (IReadOnlyList<string> row in rows)
		{
			if (row.All(string.IsNullOrWhiteSpace))
			{
				continue;
			}
			Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
			foreach (KeyValuePair<int, SoundDatasetColumnModel> item in dictionary)
			{
				item.Deconstruct(out var key, out var value);
				int num = key;
				SoundDatasetColumnModel soundDatasetColumnModel2 = value;
				string text = ((num < row.Count) ? row[num] : string.Empty);
				dictionary2[soundDatasetColumnModel2.Key] = ConvertImportedValue(soundDatasetColumnModel2.FieldType, text);
			}
			list.Add(new ImportedDataSetRow(dictionary2));
		}
		return list;
	}

	private static object? ConvertImportedValue(FieldType fieldType, string text)
	{
		string text2 = text?.Trim() ?? string.Empty;
		object result;
		if (string.IsNullOrWhiteSpace(text2))
		{
			if (1 == 0)
			{
			}
			result = fieldType switch
			{
				FieldType.Boolean => false, 
				FieldType.Int32 => 0, 
				FieldType.Int64 => 0L, 
				FieldType.UInt32 => 0u, 
				FieldType.UInt64 => 0uL, 
				FieldType.Float32 => 0f, 
				FieldType.Float64 => 0.0, 
				FieldType.String => string.Empty, 
				FieldType.Pointer => Guid.Empty, 
				_ => string.Empty, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
		if (1 == 0)
		{
		}
		switch (fieldType)
		{
		case FieldType.Boolean:
		{
			int num;
			if (!bool.TryParse(text2, out var result2))
			{
				if (!(text2 == "1"))
				{
					if (!(text2 == "0"))
					{
						throw new FormatException("Expected a boolean value but found '" + text2 + "'.");
					}
					num = 0;
				}
				else
				{
					num = 1;
				}
			}
			else
			{
				num = (result2 ? 1 : 0);
			}
			result = (byte)num != 0;
			break;
		}
		case FieldType.Int32:
			result = int.Parse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture);
			break;
		case FieldType.Int64:
			result = long.Parse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture);
			break;
		case FieldType.UInt32:
			result = uint.Parse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture);
			break;
		case FieldType.UInt64:
			result = ulong.Parse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture);
			break;
		case FieldType.Float32:
			result = float.Parse(text2, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
			break;
		case FieldType.Float64:
			result = double.Parse(text2, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
			break;
		case FieldType.String:
			result = text2;
			break;
		case FieldType.Pointer:
			result = Guid.Parse(text2);
			break;
		default:
			result = text2;
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string BuildCsv(SoundDatasetSheetModel sheet)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(string.Join(",", sheet.Columns.Select((SoundDatasetColumnModel column) => EscapeCsv(column.Header))));
		foreach (SoundDatasetRowModel row in sheet.Rows)
		{
			stringBuilder.AppendLine(string.Join(",", row.Cells.Select((SoundDatasetCellModel cell) => EscapeCsv(cell.ValueText))));
		}
		return stringBuilder.ToString();
	}

	private static void WriteWorksheet(IXLWorksheet worksheet, SoundDatasetSheetModel sheet)
	{
		for (int i = 0; i < sheet.Columns.Count; i++)
		{
			worksheet.Cell(1, i + 1).Value = sheet.Columns[i].Header;
			worksheet.Cell(1, i + 1).Style.Font.Bold = true;
		}
		for (int j = 0; j < sheet.Rows.Count; j++)
		{
			SoundDatasetRowModel soundDatasetRowModel = sheet.Rows[j];
			for (int k = 0; k < soundDatasetRowModel.Cells.Count; k++)
			{
				worksheet.Cell(j + 2, k + 1).Value = soundDatasetRowModel.Cells[k].ValueText;
			}
		}
		worksheet.Columns().AdjustToContents();
	}

	private static List<IReadOnlyList<string>> ParseDelimitedRows(string input, char delimiter)
	{
		List<IReadOnlyList<string>> list = new List<IReadOnlyList<string>>();
		List<string> list2 = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		for (int i = 0; i < input.Length; i++)
		{
			char c = input[i];
			if (flag)
			{
				if (c == '"' && i + 1 < input.Length && input[i + 1] == '"')
				{
					stringBuilder.Append('"');
					i++;
				}
				else if (c == '"')
				{
					flag = false;
				}
				else
				{
					stringBuilder.Append(c);
				}
				continue;
			}
			if (c == '"')
			{
				flag = true;
				continue;
			}
			if (c == delimiter)
			{
				list2.Add(stringBuilder.ToString());
				stringBuilder.Clear();
				continue;
			}
			switch (c)
			{
			case '\n':
				list2.Add(stringBuilder.ToString());
				stringBuilder.Clear();
				list.Add(list2.ToArray());
				list2 = new List<string>();
				break;
			default:
				stringBuilder.Append(c);
				break;
			case '\r':
				break;
			}
		}
		if (stringBuilder.Length > 0 || list2.Count > 0)
		{
			list2.Add(stringBuilder.ToString());
			list.Add(list2.ToArray());
		}
		return list;
	}

	private static char DetectCsvDelimiter(string input)
	{
		string source = input.Split(new char[2] { '\r', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
		int num = source.Count((char ch) => ch == ',');
		int num2 = source.Count((char ch) => ch == ';');
		return (num2 > num) ? ';' : ',';
	}

	private static string EscapeCsv(string? value)
	{
		string text = value ?? string.Empty;
		if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
		{
			return text;
		}
		return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
	}

	private static string NormalizeHeader(string value)
	{
		return (value ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
	}

	private static string SanitizeWorksheetName(string value)
	{
		char[] array = new char[7] { '\\', '/', '*', '[', ']', ':', '?' };
		string text = value;
		char[] array2 = array;
		foreach (char oldChar in array2)
		{
			text = text.Replace(oldChar, '_');
		}
		if (text.Length == 0)
		{
			text = "DataSet";
		}
		return (text.Length <= 31) ? text : text.Substring(0, 31);
	}

	private static string BuildSourceSummary(SoundAssetState state)
	{
		if (state.ResEntry != null)
		{
			return "Source: RES " + state.ResEntry.Name;
		}
		Guid? bankContainerChunkId = state.BankContainerChunkId;
		if (bankContainerChunkId.HasValue)
		{
			Guid valueOrDefault = bankContainerChunkId.GetValueOrDefault();
			if (true)
			{
				return $"Source: bank chunk {valueOrDefault}";
			}
		}
		if (state.Chunks.Count == 1)
		{
			return "Source: " + state.Chunks[0].Name;
		}
		return $"Source: {state.Chunks.Count:N0} chunk(s)";
	}

	private static bool IsModifiedFromState(SoundAssetState state)
	{
		if (AssetManager.IsEbxModified(state.Entry.Name))
		{
			return true;
		}
		if (state.ResEntry != null && AssetManager.IsResModified(state.ResEntry.ResRid))
		{
			return true;
		}
		Guid? bankContainerChunkId = state.BankContainerChunkId;
		if (bankContainerChunkId.HasValue)
		{
			Guid valueOrDefault = bankContainerChunkId.GetValueOrDefault();
			if (AssetManager.IsChunkModified(valueOrDefault))
			{
				return true;
			}
		}
		return state.Chunks.Any((SoundChunkBinding chunk) => AssetManager.IsChunkModified(chunk.ChunkId));
	}

	private static int GetRowCount(DataSet dataSet)
	{
		return Math.Max(dataSet.NumElems, (from field in dataSet.Fields.Concat(dataSet.IndexColumns)
			select field.Values.Count).DefaultIfEmpty(0).Max());
	}

	private static string ResolveHashName(uint hash)
	{
		string value;
		return KnownStringHashes.NewWaveHashLookup.TryGetValue(hash, out value) ? value : $"0x{hash:X8}";
	}

	private static string ResolveIdentifier(uint hash)
	{
		string value;
		return KnownStringHashes.NewWaveHashLookup.TryGetValue(hash, out value) ? value : $"0x{hash:X8}";
	}

	private static string BuildRowName(SoundDatasetRowModel row)
	{
		SoundDatasetCellModel soundDatasetCellModel = row.Cells.FirstOrDefault((SoundDatasetCellModel cell) => cell.FieldType == FieldType.String && !string.IsNullOrWhiteSpace(cell.ValueText));
		if (soundDatasetCellModel != null)
		{
			return soundDatasetCellModel.ValueText;
		}
		SoundDatasetCellModel soundDatasetCellModel2 = row.Cells.FirstOrDefault((SoundDatasetCellModel cell) => !string.IsNullOrWhiteSpace(cell.ValueText));
		return (soundDatasetCellModel2 != null) ? (soundDatasetCellModel2.Header + ": " + soundDatasetCellModel2.ValueText) : $"Row {row.Index}";
	}

	private static string FormatSeconds(double seconds)
	{
		return FormatTimeline(TimeSpan.FromSeconds(Math.Max(0.0, seconds)));
	}

	private static string FormatTimeline(TimeSpan time)
	{
		return (time.TotalHours >= 1.0) ? time.ToString("h\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture) : time.ToString("m\\:ss\\.fff", CultureInfo.InvariantCulture);
	}

	private SpsSoundHeader? TryReadHeaderCached(Guid chunkId, Segment segment)
	{
		(Guid, uint) key = (chunkId, segment.SamplesOffset & 0xFFFFFFFCu);
		if (m_segmentHeaderCache.TryGetValue(key, out SpsSoundHeader value))
		{
			return value;
		}
		SpsSoundHeader spsSoundHeader = null;
		try
		{
			ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(chunkId);
			if (chunkAssetEntry == null)
			{
				m_segmentHeaderCache[key] = null;
				return null;
			}
			using MemoryStream memoryStream = new MemoryStream(AssetManager.GetAsset(chunkAssetEntry).ToArray(), writable: false);
			memoryStream.Position = key.Item2;
			spsSoundHeader = SpsSoundHeader.LoadFrom(memoryStream);
		}
		catch
		{
		}
		m_segmentHeaderCache[key] = spsSoundHeader;
		return spsSoundHeader;
	}

	private SoundDatasetSheetModel? ResolveInitialDataSetSelection(string? dataSetId)
	{
		if (DataSets.Count == 0)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(dataSetId))
		{
			SoundDatasetSheetModel soundDatasetSheetModel = DataSets.FirstOrDefault((SoundDatasetSheetModel sheet) => string.Equals(sheet.Id, dataSetId, StringComparison.OrdinalIgnoreCase));
			if (soundDatasetSheetModel != null)
			{
				return soundDatasetSheetModel;
			}
		}
		return DataSets.FirstOrDefault((SoundDatasetSheetModel sheet) => string.Equals(sheet.DisplayName, "Selection", StringComparison.OrdinalIgnoreCase)) ?? DataSets[0];
	}

	private void UpdateVariationPlaybackSummary()
	{
		foreach (SoundVariationItemModel variation in Variations)
		{
			bool flag = variation == m_playingVariation;
			variation.HasActivePlayback = flag && IsPlaybackActive;
			variation.PlaybackSummaryText = (flag ? ("Playback: " + PlaybackStateText) : "Ready");
		}
	}

	private void UpdatePlaybackTimeline(TimeSpan current, TimeSpan total)
	{
		PlaybackTimelineText = FormatTimeline(current) + " / " + FormatTimeline(total);
	}

	private void UpdateRowState(SoundDatasetRowModel row)
	{
		bool flag = row.Cells.Any((SoundDatasetCellModel cell) => cell.IsDirty);
		bool flag2 = row.Cells.Any((SoundDatasetCellModel cell) => cell.HasValidationError);
		row.IsDirty = flag || row.IsNewRow;
		row.StateText = (flag2 ? "Validation error" : (row.IsNewRow ? "New row" : (row.IsDirty ? "Modified" : "Clean")));
	}

	private void UpdateDataSetState(SoundDatasetSheetModel sheet)
	{
		if (sheet.IsLoaded)
		{
			sheet.RefreshRowCount();
		}
		sheet.HasPendingChanges = sheet.Rows.Any((SoundDatasetRowModel row) => row.IsDirty || row.IsNewRow);
		sheet.PendingChangesText = (sheet.HasPendingChanges ? $"{sheet.Rows.Count((SoundDatasetRowModel row) => row.IsDirty || row.IsNewRow):N0} row(s) changed" : "No pending changes");
	}

	private bool ValidateAllDataSetRows()
	{
		bool flag = true;
		foreach (SoundDatasetSheetModel dataSet in DataSets)
		{
			EnsureDataSetLoaded(dataSet);
			foreach (SoundDatasetRowModel row in dataSet.Rows)
			{
				foreach (SoundDatasetCellModel cell in row.Cells)
				{
					flag &= cell.Validate();
				}
				row.RefreshValidationState();
				UpdateRowState(row);
			}
			UpdateDataSetState(dataSet);
		}
		UpdateDataSetEditorSummary();
		RaiseComputedPropertyChanges();
		return flag;
	}

	private void UpdateDataSetEditorSummary()
	{
		if (SelectedDataSetRow == null)
		{
			DataSetEditorSummaryText = "Select a data set row to inspect or edit.";
			return;
		}
		int num = SelectedDataSetRow.Cells.Count((SoundDatasetCellModel cell) => cell.HasValidationError);
		DataSetEditorSummaryText = ((num == 0) ? $"{SelectedDataSetRow.Cells.Count:N0} field(s) ready" : $"{num:N0} validation error(s)");
	}

	private void RefreshSelectionSummary()
	{
		if (IsVariationsMode)
		{
			SelectionSummaryText = ((SelectedSegment != null) ? (SelectedSegment.Name + " in " + (SelectedVariation?.Name ?? "selection")) : ((SelectedVariation != null) ? SelectedVariation.Name : "Select a variation or segment."));
		}
		else
		{
			SelectionSummaryText = ((SelectedDataSetRow != null) ? (SelectedDataSet?.DisplayName + ": " + SelectedDataSetRow.Name) : ((SelectedDataSet != null) ? SelectedDataSet.DisplayName : "Select a data set."));
		}
	}

	private void RaiseComputedPropertyChanges()
	{
		OnPropertyChanged("IsVariationsMode");
		OnPropertyChanged("IsDataSetsMode");
		OnPropertyChanged("CanPlaySelection");
		OnPropertyChanged("CanExportSelection");
		OnPropertyChanged("CanImportSelection");
		OnPropertyChanged("CanSaveAsset");
		OnPropertyChanged("CanAddItem");
		OnPropertyChanged("CanRemoveItem");
		OnPropertyChanged("CanStopPlayback");
		OnPropertyChanged("CanPausePlayback");
		OnPropertyChanged("CanResumePlayback");
		OnPropertyChanged("CanPlayPreviousSegment");
		OnPropertyChanged("CanPlayNextSegment");
		OnPropertyChanged("CanExportBulk");
		OnPropertyChanged("CanImportBulk");
		OnPropertyChanged("CanExportDataSet");
		OnPropertyChanged("CanImportDataSet");
		OnPropertyChanged("HasValidationErrors");
		OnPropertyChanged("IsPlaybackProgressIndeterminate");
		OnPropertyChanged("CanToggleMode");
		OnPropertyChanged("ModeButtonText");
		OnPropertyChanged("ModeHeaderText");
		OnPropertyChanged("AddButtonText");
		OnPropertyChanged("SaveButtonText");
		OnPropertyChanged("VariationCountText");
		OnPropertyChanged("DataSetCountText");
		OnPropertyChanged("SelectedVariationSegmentCountText");
		OnPropertyChanged("SelectedDataSetRowCountText");
		OnPropertyChanged("HasUnsavedChanges");
		OnPropertyChanged("CanSaveDocument");
		OnPropertyChanged("CanExportDocument");
		OnPropertyChanged("DirtyStateText");
	}

	private static string SafeName(string value)
	{
		char[] invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			value = value.Replace(oldChar, '_');
		}
		return value;
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnModeChanged(SoundEditorMode value)
	{
		if (value == SoundEditorMode.DataSets && DataSets.Count == 0)
		{
			StatusText = "This asset does not expose editable sound data sets.";
		}
		RaiseComputedPropertyChanges();
		RefreshSelectionSummary();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnSelectedVariationChanged(SoundVariationItemModel? value)
	{
		SelectedVariationSegments.Clear();
		foreach (SoundVariationItemModel variation in Variations)
		{
			variation.IsSelected = variation == value;
		}
		if (value != null)
		{
			foreach (SoundSegmentItemModel segment in value.Segments)
			{
				SelectedVariationSegments.Add(segment);
			}
			if (SelectedSegment == null || !SelectedVariationSegments.Contains(SelectedSegment))
			{
				SelectedSegment = SelectedVariationSegments.FirstOrDefault();
			}
			SelectedVariationSummaryText = value.Name + " | " + value.SelectionText;
		}
		else
		{
			SelectedSegment = null;
			SelectedVariationSummaryText = "Select a variation.";
		}
		UpdateVariationPlaybackSummary();
		RefreshSelectionSummary();
		RaiseComputedPropertyChanges();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnSelectedSegmentChanged(SoundSegmentItemModel? value)
	{
		foreach (SoundSegmentItemModel segmentRow in SegmentRows)
		{
			segmentRow.IsSelected = segmentRow == value;
			if (segmentRow != m_playingSegment)
			{
				segmentRow.IsCurrent = false;
			}
		}
		if (value != null)
		{
			SoundVariationItemModel soundVariationItemModel = Variations.FirstOrDefault((SoundVariationItemModel variation) => variation.ListIndex == value.VariationListIndex);
			if (SelectedVariation != soundVariationItemModel)
			{
				SelectedVariation = soundVariationItemModel;
			}
		}
		SelectedSegmentSummaryText = ((value == null) ? "Select a segment." : (value.Name + " | " + value.DurationText));
		if (value != null && value != m_playingSegment && !IsPlaybackActive)
		{
			value.ResetPlaybackState();
		}
		RefreshSelectionSummary();
		RaiseComputedPropertyChanges();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnSelectedDataSetChanged(SoundDatasetSheetModel? value)
	{
		if (value != null)
		{
			EnsureDataSetLoaded(value);
			SelectedDataSetRow = value.Rows.FirstOrDefault();
			SelectedDataSetSummaryText = value.DisplayName + " | " + value.RowCountText;
		}
		else
		{
			SelectedDataSetRow = null;
			SelectedDataSetSummaryText = "Select a data set.";
		}
		UpdateDataSetEditorSummary();
		RefreshSelectionSummary();
		RaiseComputedPropertyChanges();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnSelectedDataSetRowChanged(SoundDatasetRowModel? value)
	{
		SelectedDataSetRowSummaryText = ((value == null) ? "Select a row." : (value.Name + " | " + value.StateText));
		UpdateDataSetEditorSummary();
		RefreshSelectionSummary();
		RaiseComputedPropertyChanges();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnPlaybackPositionChanged(double value)
	{
		if (!m_isSeeking && m_audioReader != null && IsPlaybackActive)
		{
			double num = Math.Clamp(value, 0.0, PlaybackMaximum);
			m_audioReader.CurrentTime = TimeSpan.FromSeconds(num);
			UpdatePlaybackTimeline(TimeSpan.FromSeconds(num), TimeSpan.FromSeconds(PlaybackMaximum));
			if (m_playingSegment != null)
			{
				m_playingSegment.ProgressValue = num;
				m_playingSegment.ElapsedText = FormatTimeline(TimeSpan.FromSeconds(num));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnHasPendingDataSetEditsChanged(bool value)
	{
		RaiseComputedPropertyChanges();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.2.0.0")]
	private void OnIsModifiedChanged(bool value)
	{
		RaiseComputedPropertyChanges();
	}
}
