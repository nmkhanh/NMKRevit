using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace NMKRevit.NMKPrint.Models
{
  public class PrintLogEntry : ObservableObject
  {
    private static readonly MediaBrush PrimaryBrush = CreateBrush("#2FD25F");
    private static readonly MediaBrush WarningBrush = CreateBrush("#FF7A00");
    private static readonly MediaBrush ErrorBrush = CreateBrush("#FF4500");
    private static readonly MediaBrush DarkTextBrush = CreateBrush("#111827");

    private DateTime _time = DateTime.Now;
    private LogLevel _level;
    private string _format = string.Empty;
    private string _sheetNumber = string.Empty;
    private string _sheetName = string.Empty;
    private string _message = string.Empty;
    private string _filePath = string.Empty;
    private double _progressValue;
    private bool _isJob;
    private bool _isIndeterminate;

    public PrintLogEntry(LogLevel level, string message, string? filePath = null)
    {
      Update(level, message, filePath);
    }

    public DateTime Time
    {
      get => _time;
      private set => SetProperty(ref _time, value);
    }

    public LogLevel Level
    {
      get => _level;
      private set
      {
        if (SetProperty(ref _level, value))
        {
          OnPropertyChanged(nameof(Foreground));
          OnPropertyChanged(nameof(IsProgressVisible));
          OnPropertyChanged(nameof(IsStatusVisible));
          OnPropertyChanged(nameof(IsIndeterminate));
          OnPropertyChanged(nameof(ProgressText));
          OnPropertyChanged(nameof(ProgressTextForeground));
        }
      }
    }

    public string Format
    {
      get => _format;
      private set => SetProperty(ref _format, value);
    }

    public string SheetNumber
    {
      get => _sheetNumber;
      private set => SetProperty(ref _sheetNumber, value);
    }

    public string SheetName
    {
      get => _sheetName;
      private set => SetProperty(ref _sheetName, value);
    }

    public string Message
    {
      get => _message;
      private set
      {
        if (SetProperty(ref _message, value))
        {
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    public string FilePath
    {
      get => _filePath;
      private set => SetProperty(ref _filePath, value ?? string.Empty);
    }

    public double ProgressValue
    {
      get => _progressValue;
      set
      {
        if (SetProperty(ref _progressValue, value))
        {
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    public bool IsJob
    {
      get => _isJob;
      private set
      {
        if (SetProperty(ref _isJob, value))
        {
          OnPropertyChanged(nameof(IsProgressVisible));
          OnPropertyChanged(nameof(IsStatusVisible));
        }
      }
    }

    public bool IsProgressVisible => IsJob && (Level == LogLevel.Printing || Level == LogLevel.Done);
    public bool IsStatusVisible => !IsProgressVisible;

    public bool IsIndeterminate
    {
      get => _isIndeterminate;
      private set
      {
        if (SetProperty(ref _isIndeterminate, value))
        {
          OnPropertyChanged(nameof(ProgressText));
        }
      }
    }

    public string ProgressText => IsIndeterminate ? Message : Level == LogLevel.Done ? "Done" : $"{ProgressValue:0} %";

    public MediaBrush ProgressTextForeground => Level == LogLevel.Done ? MediaBrushes.White : DarkTextBrush;

    public MediaBrush Foreground => Level switch
    {
      LogLevel.Printing => PrimaryBrush,
      LogLevel.Done => PrimaryBrush,
      LogLevel.Warning => WarningBrush,
      LogLevel.Error => ErrorBrush,
      _ => MediaBrushes.DimGray
    };

    public void SetJob(string format, string sheetNumber, string sheetName)
    {
      IsJob = true;
      Format = format;
      SheetNumber = sheetNumber;
      SheetName = sheetName;
    }

    public void SetStage(string format, string label)
    {
      IsJob = true;
      Format = format;
      SheetNumber = label;
      SheetName = label;
    }

    public void Update(LogLevel level, string message, string? filePath, bool isIndeterminate = false)
    {
      Time = DateTime.Now;
      Level = level;
      Message = message;
      IsIndeterminate = isIndeterminate;
      FilePath = filePath ?? string.Empty;
    }

    private static MediaBrush CreateBrush(string hex)
    {
      var brush = new MediaSolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(hex));
      brush.Freeze();
      return brush;
    }
  }
}
