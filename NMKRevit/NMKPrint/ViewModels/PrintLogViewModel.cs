using CommunityToolkit.Mvvm.ComponentModel;
using NMKRevit.NMKPrint.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WpfApplication = System.Windows.Application;

namespace NMKRevit.NMKPrint.ViewModels
{
  public partial class PrintLogViewModel : ObservableObject
  {
    public ObservableCollection<PrintLogEntry> Entries { get; } = new();

    public void Add(LogLevel level, string message, string? filePath = null)
    {
      OnUiThread(() => Entries.Add(new PrintLogEntry(level, message, filePath)));
    }

    public Task UpdateJobAsync(PrintJob job, LogLevel level, string message, string? filePath = null)
    {
      if (level == LogLevel.Printing)
      {
        OnUiThread(() =>
        {
          var entry = new PrintLogEntry(level, message, filePath);
          entry.SetJob(job.Format.ToString(), job.Item.Number, job.Item.Name);
          Entries.Add(entry);
        });
      }
      else if (level == LogLevel.Error)
      {
        OnUiThread(() =>
        {
          var entry = new PrintLogEntry(level, message, filePath);
          entry.SetJob(job.Format.ToString(), job.Item.Number, job.Item.Name);
          Entries.Add(entry);
        });
      }

      return Task.CompletedTask;
    }

    public Task UpdateStageAsync(string key, string format, string label, LogLevel level, string message, string? filePath = null)
    {
      if (level == LogLevel.Printing || level == LogLevel.Error)
      {
        OnUiThread(() =>
        {
          var entry = new PrintLogEntry(level, message, filePath);
          entry.SetStage(format, label);
          Entries.Add(entry);
        });
      }

      return Task.CompletedTask;
    }

    public void Clear()
    {
      OnUiThread(() =>
      {
        Entries.Clear();
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
  }
}
