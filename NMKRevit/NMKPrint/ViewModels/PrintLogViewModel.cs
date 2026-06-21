using CommunityToolkit.Mvvm.ComponentModel;
using NMKRevit.NMKPrint.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WpfApplication = System.Windows.Application;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class PrintLogViewModel : ObservableObject
  {
    private readonly Dictionary<string, PrintLogEntry> _jobEntries = new();
    private readonly Dictionary<string, PrintLogEntry> _stageEntries = new();
    private readonly Random _random = new();

    public ObservableCollection<PrintLogEntry> Entries { get; } = new();

    public void Add(LogLevel level, string message, string? filePath = null)
    {
      OnUiThread(() => Entries.Add(new PrintLogEntry(level, message, filePath)));
    }

    public void UpdateJob(PrintJob job, LogLevel level, string message, string? filePath = null)
    {
      OnUiThread(() =>
      {
        PrintLogEntry entry = GetOrCreateJobEntry(job, level, message, filePath);
        entry.Update(level, message, filePath);
      });
    }

    public async Task UpdateJobAsync(PrintJob job, LogLevel level, string message, string? filePath = null)
    {
      PrintLogEntry entry = OnUiThread(() => GetOrCreateJobEntry(job, level, message, filePath));
      if (level == LogLevel.Printing)
      {
        OnUiThread(() =>
        {
          entry.Update(level, message, filePath);
          entry.ProgressValue = 0;
        });
        await AnimateProgressAsync(entry, _random.Next(40, 61));
        return;
      }

      if (level == LogLevel.Done)
      {
        await AnimateProgressAsync(entry, 100);
        OnUiThread(() =>
        {
          entry.ProgressValue = 100;
          entry.Update(level, message, filePath);
        });
        return;
      }

      OnUiThread(() => entry.Update(level, message, filePath));
    }

    public Task UpdateStageAsync(string key, string format, string label, LogLevel level, string message, string? filePath = null)
    {
      OnUiThread(() =>
      {
        PrintLogEntry entry = GetOrCreateStageEntry(key, format, label, level, message, filePath);
        if (level == LogLevel.Printing)
        {
          entry.ProgressValue = 0;
          entry.Update(level, message, filePath, true);
          return;
        }

        if (level == LogLevel.Done)
        {
          entry.ProgressValue = 100;
          entry.Update(level, message, filePath);
          return;
        }

        entry.Update(level, message, filePath);
      });

      return Task.CompletedTask;
    }

    public void Clear()
    {
      OnUiThread(() =>
      {
        Entries.Clear();
        _jobEntries.Clear();
        _stageEntries.Clear();
      });
    }

    private static void OnUiThread(Action action)
    {
      if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
      {
        dispatcher.Invoke(action);
        return;
      }

      action();
    }

    private static T OnUiThread<T>(Func<T> action)
    {
      if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
      {
        return dispatcher.Invoke(action);
      }

      return action();
    }

    private PrintLogEntry GetOrCreateJobEntry(PrintJob job, LogLevel level, string message, string? filePath)
    {
      string key = $"{job.Format}:{job.Item.Id}";
      if (_jobEntries.TryGetValue(key, out PrintLogEntry? entry))
      {
        return entry;
      }

      entry = new PrintLogEntry(level, message, filePath);
      entry.SetJob(job.Format.ToString(), job.Item.Number, job.Item.Name);
      _jobEntries[key] = entry;
      Entries.Add(entry);
      return entry;
    }

    private PrintLogEntry GetOrCreateStageEntry(string key, string format, string label, LogLevel level, string message, string? filePath)
    {
      if (_stageEntries.TryGetValue(key, out PrintLogEntry? entry))
      {
        return entry;
      }

      entry = new PrintLogEntry(level, message, filePath);
      entry.SetStage(format, label);
      _stageEntries[key] = entry;
      Entries.Add(entry);
      return entry;
    }

    private async Task AnimateProgressAsync(PrintLogEntry entry, double target)
    {
      while (entry.ProgressValue < target)
      {
        double next = Math.Min(target, entry.ProgressValue + 2);
        OnUiThread(() => entry.ProgressValue = next);
        await Task.Delay(5);
      }
    }
  }
}
