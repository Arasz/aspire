// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private readonly List<MenuButtonItem> _menuItems = [];
    private string? _previousSelectedRunId;
    private string RunSelectTitle => Loc[nameof(LayoutResources.DashboardRunSelectTitle)];
    private string RunSelectAccessibleLabel => Loc[nameof(LayoutResources.DashboardRunSelectAccessibleLabel), SelectedRunText];
    private string SelectedRunText => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, SelectedRunStartedAtUtc.UtcDateTime);

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public DateTimeOffset SelectedRunStartedAtUtc { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    protected override void OnParametersSet()
    {
        if (_previousSelectedRunId is not null &&
            !string.Equals(_previousSelectedRunId, SelectedRunId, StringComparison.Ordinal))
        {
            _menuItems.Clear();
        }
        _previousSelectedRunId = SelectedRunId;
    }

    private void LoadRuns()
    {
        var runs = RunStore.GetRuns();
        _menuItems.Clear();
        foreach (var run in runs)
        {
            _menuItems.Add(new MenuButtonItem
            {
                Text = FormatRunOption(run),
                Role = MenuItemRole.MenuItemCheckbox,
                Checked = string.Equals(run.RunId, SelectedRunId, StringComparison.Ordinal),
                OnClick = () => SelectedRunIdChanged.InvokeAsync(run.IsCurrent ? null : run.RunId)
            });

            if (run.IsCurrent && runs.Any(candidate => !candidate.IsCurrent))
            {
                _menuItems.Add(new MenuButtonItem { IsDivider = true });
            }
        }
    }

    private string FormatRunOption(DashboardRunDescriptor run)
    {
        if (run.IsCurrent)
        {
            return Loc[nameof(LayoutResources.DashboardRunSelectCurrent)];
        }

        return FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, run.StartedAtUtc.UtcDateTime);
    }
}