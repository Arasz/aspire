// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

/// <summary>
/// Automated WCAG 2.0 / 2.1 (levels A and AA) accessibility checks for the dashboard's primary
/// pages, driven by the axe-core engine (via the MIT-licensed <c>Deque.AxeCore.Playwright</c>
/// wrapper) against a real dashboard server. Both the light and dark themes are scanned because
/// color-contrast outcomes differ between them, the primary page is additionally scanned across
/// mobile, tablet and desktop viewports, and code block syntax colors are checked for AA contrast
/// in both themes (a surface axe can't reach on its own).
/// </summary>
[RequiresFeature(TestFeature.Playwright)]
public sealed class AccessibilityTests : PlaywrightTestsBase<AccessibilityTests.AccessibilityDashboardServerFixture>
{
    // WCAG 2.0 and 2.1, levels A and AA - the conformance target the dashboard commits to. axe's
    // "best-practice" and experimental rule sets are intentionally excluded so the gate tracks an
    // actual accessibility standard rather than axe's opinionated extras.
    // See https://github.com/dequelabs/axe-core/blob/develop/doc/API.md#axe-core-tags.
    private static readonly string[] s_wcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

    // axe impact levels that fail the build. Serious and critical issues are unambiguous barriers
    // for assistive-technology users. Moderate/minor findings are still reported in the failure
    // message (when a serious/critical issue trips the gate) but don't fail on their own, which
    // keeps the gate stable against third-party (Fluent UI) shadow-DOM churn we don't control.
    private static readonly HashSet<string> s_failingImpacts = new(StringComparer.OrdinalIgnoreCase) { "serious", "critical" };

    // Rule IDs intentionally excluded from the gate. Keep this empty unless a violation is a
    // confirmed false positive or lives entirely in third-party (Fluent UI) shadow DOM that
    // cannot be fixed from this repo; document each entry with a tracking issue link when added.
    private static readonly HashSet<string> s_allowedRuleIds = new(StringComparer.OrdinalIgnoreCase);

    // WCAG 2.0 SC 1.4.3 (AA) minimum contrast ratio for normal-size text.
    private const double WcagAaContrastMinimum = 4.5;

    // Mirrors ViewportInformation.MobileCutoffPixelWidth: at or below this width the dashboard renders
    // its mobile chrome (the page title is not teleported into the top bar). Kept as a local literal so
    // the test doesn't depend on dashboard internals.
    private const int MobileCutoffPixelWidth = 768;

    // The default desktop viewport used by the per-theme matrix and the code block contrast checks.
    private static readonly ViewportSize s_desktopViewport = new() { Width = 1280, Height = 900 };

    public AccessibilityTests(AccessibilityDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/", "Light")]
    [InlineData("/", "Dark")]
    [InlineData("/consolelogs", "Light")]
    [InlineData("/consolelogs", "Dark")]
    [InlineData("/structuredlogs", "Light")]
    [InlineData("/structuredlogs", "Dark")]
    [InlineData("/traces", "Light")]
    [InlineData("/traces", "Dark")]
    [InlineData("/metrics", "Light")]
    [InlineData("/metrics", "Dark")]
    public Task DashboardPage_HasNoSeriousOrCriticalWcagViolations(string relativeUrl, string theme)
        => AssertNoBlockingWcagViolationsAsync(relativeUrl, theme, s_desktopViewport);

    // Responsive coverage: re-run the same WCAG gate on the primary page across mobile, tablet and
    // desktop widths so layout-sensitive barriers - reflow, focus order, and content that overlaps or
    // is clipped once the app switches to its mobile chrome (at/below MobileCutoffPixelWidth) - are
    // caught in addition to the per-theme desktop matrix above. Light theme only; color-contrast, the
    // theme-sensitive axis, is already exercised in both themes by the matrix above.
    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Mobile", 375, 812)]
    [InlineData("Tablet", 768, 1024)]
    [InlineData("Desktop", 1280, 900)]
    public Task DashboardHomePage_IsAccessibleAcrossViewports(string viewportName, int width, int height)
        => AssertNoBlockingWcagViolationsAsync("/", "Light", new ViewportSize { Width = width, Height = height }, viewportName);

    /// <summary>
    /// Verifies that every highlight.js syntax-token color used in code blocks (the text visualizer and
    /// rendered markdown) meets the WCAG 2.0 AA 4.5:1 minimum contrast against the code block
    /// background, in both themes. axe's own color-contrast rule can't cover these because a
    /// syntax-highlighted block isn't present on the scanned pages, so this renders an off-screen block
    /// that mirrors the dialog's DOM, reads the computed colors, and asserts the ratio here.
    /// </summary>
    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task CodeblockSyntaxColors_MeetWcagAaContrast(string theme)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();

        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = baseUrl,
            ViewportSize = s_desktopViewport
        });

        await context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = theme, Url = baseUrl }]);

        var page = await context.NewPageAsync();
        await page.GotoAsync("/").DefaultTimeout();
        await page.WaitForSelectorAsync("body:not(.before-upgrade)").DefaultTimeout();
        await page.WaitForSelectorAsync(
            $"html[data-theme='{theme.ToLowerInvariant()}']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached }).DefaultTimeout();

        var probeJson = await page.EvaluateAsync<string>(CodeblockColorProbeScript);
        using var probe = JsonDocument.Parse(probeJson);
        var root = probe.RootElement;
        var background = ReadRgb(root.GetProperty("bg"));

        var failures = new List<string>();
        foreach (var token in root.GetProperty("tokens").EnumerateArray())
        {
            var foreground = ReadRgb(token);
            var ratio = ContrastRatio(foreground, background);
            if (ratio < WcagAaContrastMinimum)
            {
                failures.Add(
                    $"  {token.GetProperty("name").GetString()}: {ratio:F2}:1 " +
                    $"(text rgb({foreground.R},{foreground.G},{foreground.B}) on background rgb({background.R},{background.G},{background.B}))");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} code block syntax color(s) fall below the WCAG AA {WcagAaContrastMinimum:F1}:1 minimum " +
            $"in the {theme} theme:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private async Task AssertNoBlockingWcagViolationsAsync(string relativeUrl, string theme, ViewportSize viewport, string? viewportLabel = null)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();

        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = baseUrl,
            ViewportSize = viewport
        });

        // The dashboard resolves its theme from the `currentTheme` cookie on boot (see
        // wwwroot/js/app-theme.js). Seeding it before the first navigation makes the scanned
        // theme deterministic instead of depending on the OS/browser "System" preference.
        await context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = theme, Url = baseUrl }]);

        var page = await context.NewPageAsync();
        await page.GotoAsync(relativeUrl).DefaultTimeout();

        // Wait until Blazor has upgraded the Fluent web components - app.js removes the
        // `before-upgrade` class from <body> once the components are ready (before then <body> is
        // visibility:hidden). Scanning earlier would audit the pre-hydration shell.
        await page.WaitForSelectorAsync("body:not(.before-upgrade)").DefaultTimeout();

        // Confirm the requested theme actually applied (app-theme.js sets data-theme on <html>),
        // otherwise a contrast scan could silently run against the wrong palette.
        await page.WaitForSelectorAsync(
            $"html[data-theme='{theme.ToLowerInvariant()}']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached }).DefaultTimeout();

        // Readiness signal that content has rendered. On desktop the page title teleports into the top
        // bar as <h1 class="page-header"> (AspirePageContentLayout); at/below MobileCutoffPixelWidth
        // that teleport doesn't happen, so fall back to the always-present <main> region.
        if (viewport.Width > MobileCutoffPixelWidth)
        {
            await Assertions.Expect(page.Locator("h1.page-header")).ToBeVisibleAsync();
        }
        else
        {
            await Assertions.Expect(page.Locator("main")).ToBeVisibleAsync();
        }

        // Collapse all CSS animations/transitions to their end state before scanning. axe's
        // color-contrast check multiplies an element's foreground by its (and its ancestors')
        // computed opacity, so if it samples while something is still animating in - e.g. the
        // FluentMessageBar fades in via a 1.5s `fadein` opacity animation - it reads a washed-out
        // effective color (settled text blended toward the background) and reports a false failure.
        // Forcing animation/transition durations to 0 pins every element at its resting opacity/color.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = "*, *::before, *::after { animation-duration: 0s !important; animation-delay: 0s !important; transition-duration: 0s !important; transition-delay: 0s !important; }"
        });

        var axeResults = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. s_wcagTags] }
        });

        var blockingViolations = axeResults.Violations
            .Where(v => v.Impact is not null
                && s_failingImpacts.Contains(v.Impact)
                && !s_allowedRuleIds.Contains(v.Id))
            .ToList();

        Assert.True(
            blockingViolations.Count == 0,
            BuildFailureMessage(axeResults, blockingViolations, relativeUrl, theme, viewportLabel));
    }

    private static string BuildFailureMessage(AxeResult results, IReadOnlyList<AxeResultItem> blocking, string relativeUrl, string theme, string? viewportLabel = null)
    {
        var scanContext = viewportLabel is null ? $"{theme} theme" : $"{theme} theme, {viewportLabel} viewport";
        var sb = new StringBuilder();
        sb.AppendLine($"Found {blocking.Count} serious/critical WCAG 2.x A/AA accessibility violation(s) on '{relativeUrl}' ({scanContext}):");
        sb.AppendLine();

        foreach (var violation in blocking)
        {
            sb.AppendLine($"  [{violation.Impact}] {violation.Id}: {violation.Help}");
            sb.AppendLine($"    {violation.HelpUrl}");

            // Show a few offending nodes (selector + markup) to make the failure actionable without
            // re-running axe locally. Cap the count/length so the message stays readable. Use the
            // AxeSelector's string form rather than .Selector because Fluent UI renders into shadow
            // DOM, and .Selector throws for shadow-nested nodes (it can't be a single CSS selector).
            foreach (var node in violation.Nodes.Take(5))
            {
                sb.AppendLine($"      target: {node.Target}");
                sb.AppendLine($"      html:   {Truncate(node.Html, 200)}");

                // For color-contrast (and similar) rules, axe records the measured values - e.g.
                // foreground/background color, ratio and required ratio - in the per-node "any"
                // checks. Surfacing those messages makes the fix precise (which color, which ratio).
                foreach (var check in node.Any)
                {
                    sb.AppendLine($"      why:    {check.Message}");
                }
            }

            sb.AppendLine();
        }

        // Surface non-blocking (moderate/minor) findings and any allowlisted rules too, so the
        // failure captures the full accessibility picture for the page in one place.
        var other = results.Violations.Where(v => !blocking.Contains(v)).ToList();
        if (other.Count > 0)
        {
            sb.AppendLine($"Additional non-blocking violations ({other.Count}): "
                + string.Join(", ", other.Select(v => $"{v.Id}({v.Impact ?? "n/a"})")));
        }

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");

    private static (int R, int G, int B) ReadRgb(JsonElement element)
        => (element.GetProperty("r").GetInt32(), element.GetProperty("g").GetInt32(), element.GetProperty("b").GetInt32());

    // WCAG 2.x relative luminance and contrast ratio.
    // See https://www.w3.org/TR/WCAG21/#dfn-relative-luminance and #dfn-contrast-ratio.
    private static double ContrastRatio((int R, int G, int B) foreground, (int R, int G, int B) background)
    {
        var l1 = RelativeLuminance(foreground.R, foreground.G, foreground.B);
        var l2 = RelativeLuminance(background.R, background.G, background.B);
        var (lighter, darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(int r, int g, int b)
    {
        static double Channel(int component)
        {
            var c = component / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    // Renders an off-screen code block that mirrors the text visualizer's DOM
    // (.text-visualizer-container > .log-overflow > span.hljs) so the highlight.js token colors
    // (.hljs-*) and --codeblock-background-color resolve exactly as they do in the dialog, then returns
    // each token group's computed color plus the background as {r,g,b}. The active theme is whatever the
    // page booted with (data-theme on <html>). WCAG math is done in C# in the test. parseRgb also
    // handles Chromium's color(srgb ...) form, which color-mix() backgrounds can compute to.
    private const string CodeblockColorProbeScript = """
        () => {
            function parseRgb(s) {
                s = String(s).trim();
                let m = s.match(/rgba?\(([^)]+)\)/);
                if (m) { const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(parseFloat); return [p[0], p[1], p[2]]; }
                m = s.match(/color\(srgb\s+([^)]+)\)/);
                if (m) { const p = m[1].split(/[ \/]+/).filter(Boolean).map(parseFloat); return [Math.round(p[0] * 255), Math.round(p[1] * 255), Math.round(p[2] * 255)]; }
                throw new Error('unparseable color: ' + s);
            }
            const groups = { default: 'hljs', comment: 'hljs-comment', variable: 'hljs-variable', literal: 'hljs-literal', attribute: 'hljs-attribute', string: 'hljs-string', section: 'hljs-section', keyword: 'hljs-keyword' };
            const theme = document.documentElement.getAttribute('data-theme');
            const container = document.createElement('div');
            container.className = 'text-visualizer-container';
            container.style.cssText = 'position:fixed;left:-99999px;top:0;';
            const overflow = document.createElement('div');
            overflow.className = 'log-overflow';
            const line = document.createElement('span');
            line.className = 'log-content highlight-line hljs theme-a11y-' + theme + '-min';
            line.textContent = 'sample';
            overflow.appendChild(line);
            container.appendChild(overflow);
            document.body.appendChild(container);
            const bg = parseRgb(getComputedStyle(overflow).backgroundColor);
            const tokens = [];
            for (const name of Object.keys(groups)) {
                const cls = groups[name];
                let el;
                if (cls === 'hljs') { el = line; }
                else { el = document.createElement('span'); el.className = cls; el.textContent = 'x'; line.appendChild(el); }
                const fg = parseRgb(getComputedStyle(el).color);
                tokens.push({ name: name, r: fg[0], g: fg[1], b: fg[2] });
            }
            document.body.removeChild(container);
            return JSON.stringify({ bg: { r: bg[0], g: bg[1], b: bg[2] }, tokens: tokens });
        }
        """;

    public sealed class AccessibilityDashboardServerFixture : DashboardServerFixture
    {
        // A small but representative resource set so the resource grid, filters and pickers render
        // real content for the scan rather than an empty state.
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: "frontend",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(
                resourceName: "cache",
                resourceType: KnownResourceTypes.Container,
                state: KnownResourceState.Running),
        ];
    }
}
