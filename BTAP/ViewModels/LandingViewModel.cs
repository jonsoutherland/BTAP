using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using BTAP.Models;
using BTAP.Services;

namespace BTAP.ViewModels;

public record TemplateItem(string Name, string Sub, string Kind);

public partial class LandingViewModel : ObservableObject
{
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _activeTab = "recent";

    public ObservableCollection<RecentProject> Recents { get; }
    public ObservableCollection<RecentProject> FilteredRecents { get; }

    public LandingViewModel()
    {
        var loaded = LoadRecentsFromDisk();
        Recents         = new ObservableCollection<RecentProject>(loaded);
        FilteredRecents = new ObservableCollection<RecentProject>(loaded);
    }

    private static List<RecentProject> LoadRecentsFromDisk()
    {
        var entries = ProjectSerializer.LoadRecents();
        // Prune entries whose files no longer exist
        entries.RemoveAll(e => !string.IsNullOrEmpty(e.Path) && !System.IO.File.Exists(e.Path));
        return entries.ConvertAll(e => new RecentProject(
            e.Name,
            FormatRelativeTime(e.LastModified),
            e.Duration,
            e.Spec,
            e.Path));
    }

    public bool HasRecents => Recents.Count > 0;

    private static string FormatRelativeTime(DateTime when)
    {
        var diff = DateTime.Now - when;
        if (diff.TotalSeconds < 60)   return $"{(int)diff.TotalSeconds}s ago";
        if (diff.TotalMinutes < 60)   return $"{(int)diff.TotalMinutes} minute{((int)diff.TotalMinutes == 1 ? "" : "s")} ago";
        if (diff.TotalHours   < 24)   return $"{(int)diff.TotalHours} hour{((int)diff.TotalHours == 1 ? "" : "s")} ago";
        if (diff.TotalDays    < 2)    return "Yesterday";
        if (diff.TotalDays    < 30)   return $"{(int)diff.TotalDays} days ago";
        return when.ToString("MMM d");
    }

    public List<TemplateItem> Templates { get; } =
    [
        new("Blank project",     "Start from nothing",      "blank"),
        new("YouTube · 16:9",   "1080p · 30 fps",          "yt"),
        new("Shorts · 9:16",    "1080×1920 · 60 fps",      "short"),
        new("Square · 1:1",     "1080×1080 · 30 fps",      "sq"),
        new("Cinema · 2.39:1",  "3840×1606 · 24 fps",      "cine"),
        new("Podcast video",    "Multi-cam · 1080p",        "pod"),
    ];

    partial void OnSearchTextChanged(string value)
    {
        FilteredRecents.Clear();
        foreach (var r in Recents)
        {
            if (string.IsNullOrEmpty(value) ||
                r.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
                FilteredRecents.Add(r);
        }
    }

    public static Project CreateBlankProject() => Project.CreateDefault();

    public static Project OpenFromTemplate(TemplateItem template)
    {
        var p = Project.CreateDefault();
        p.Name = template.Name;
        (p.Width, p.Height, p.FrameRate) = template.Kind switch
        {
            "short" => (1080, 1920, 60.0),
            "sq"    => (1080, 1080, 30.0),
            "cine"  => (3840, 1606, 24.0),
            "yt"    => (1920, 1080, 30.0),
            _       => (1920, 1080, 24.0),
        };
        return p;
    }
}
