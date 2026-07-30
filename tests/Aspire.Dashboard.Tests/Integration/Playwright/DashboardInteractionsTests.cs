// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

// Functional coverage for the net-new interactive behaviors implemented purely in app.js: grid
// column auto-fit (double-click a resize handle), the parent-row ownership highlight on the
// Resources grid, and the floating scroll-to-top/bottom buttons for large scroll regions. These
// carry real runtime logic (column/track alignment, depth parsing keyed to the name indent,
// overflow/edge thresholds) and are coupled to specific markup (".resources-name-container",
// ".resize-handle", ".continuous-scroll-overflow", the depth * 16px indent). Scanning resting page
// state can't catch a regression here, so we drive the interactions and assert their DOM effects -
// which also fails loudly if any of those selectors are renamed out from under the JS.
[RequiresFeature(TestFeature.Playwright)]
public class DashboardInteractionsTests : PlaywrightTestsBase<DashboardInteractionsTests.InteractionsDashboardServerFixture>
{
    public DashboardInteractionsTests(InteractionsDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GridColumn_DoubleClickResizeHandle_AutoFitsColumnWidth()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            var grid = page.Locator(".main-grid").First;
            await Assertions.Expect(grid).ToBeVisibleAsync();

            // Guard against a Fluent UI Blazor rename of the resize handle class: the auto-fit
            // double-click handler keys off exactly these selectors, so if neither is present the
            // feature is silently dead and this count assertion surfaces it.
            var handles = page.Locator(".main-grid .resize-handle, .main-grid .col-width-draghandle");
            Assert.True(await handles.CountAsync() > 0, "Expected at least one grid resize handle to be rendered.");

            // Fluent writes the resolved template from GetGridTemplateColumns() to the table's *inline*
            // grid-template-columns, so it is already populated at rest (typically with fr/auto tracks).
            // auto-fit resolves every track to concrete px and rewrites the inline template with the
            // fitted column, so the reliable end-to-end proof is that the inline value *changes* and is
            // now an explicit px template.
            var inlineBefore = await grid.EvaluateAsync<string>("el => el.style.gridTemplateColumns");

            // The auto-fit behavior is a delegated document-level "dblclick" listener that keys off
            // e.target.closest(".resize-handle"). Dispatch the dblclick straight onto the handle element
            // rather than a pixel-precise click: the handle is a thin edge bar whose pointer-events are
            // gated, so a coordinate double-click retargets to the header behind it and never reaches the
            // handler. DispatchEvent fires a real bubbling MouseEvent on the exact element, which bubbles
            // to the document listener exactly as a user double-click on the handle would.
            await handles.First.DispatchEventAsync("dblclick");

            await page.WaitForFunctionAsync(
                @"before => {
                    const g = document.querySelector('.main-grid');
                    if (!g) { return false; }
                    const now = g.style.gridTemplateColumns;
                    return now.length > 0 && now !== before && /px/.test(now);
                }",
                inlineBefore)
                .DefaultTimeout();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ParentRow_Hover_HighlightsDescendantRows()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            var parentRow = page.Locator(".main-grid .fluent-data-grid-row", new() { HasText = InteractionsDashboardServerFixture.ParentResourceName }).First;
            var childRow = page.Locator(".main-grid .fluent-data-grid-row", new() { HasText = InteractionsDashboardServerFixture.ChildResourceName }).First;
            await Assertions.Expect(parentRow).ToBeVisibleAsync();
            await Assertions.Expect(childRow).ToBeVisibleAsync();

            // Resting state: the child row carries no ownership highlight.
            await Assertions.Expect(childRow).Not.ToHaveClassAsync(new Regex(@"\bparent-hover-descendant\b"));

            await parentRow.HoverAsync();

            // Hovering the parent tints its descendant (deeper-indent) rows so the ownership group
            // reads at a glance. This depends on the child's name-cell indent being deeper than the
            // parent's (margin-left = depth * 16px), which the nested fixture resource produces.
            await Assertions.Expect(childRow).ToHaveClassAsync(new Regex(@"\bparent-hover-descendant\b"));

            // Moving the pointer off the grid clears the highlight.
            await page.Mouse.MoveAsync(0, 0);
            await Assertions.Expect(childRow).Not.ToHaveClassAsync(new Regex(@"\bparent-hover-descendant\b"));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ActivateForOverflowingRegion_AndScrollIt()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            // The mock dashboard client serves no console-log/telemetry stream, so no page naturally
            // renders an overflowing ".continuous-scroll-overflow" (that class lives on Console logs,
            // Traces and Structured logs). Inject a representative region using the same contract
            // selector the feature discovers, so we can exercise the button feature's real logic -
            // MutationObserver discovery, overflow-threshold activation (240px), edge-threshold
            // visibility (120px) and click-to-scroll - end to end. There are no other scroll regions
            // on the Resources page, so the single ".scroll-buttons" root belongs to this region.
            await page.EvaluateAsync(@"() => {
                const region = document.createElement('div');
                region.className = 'continuous-scroll-overflow';
                region.id = 'synthetic-scroll-region';
                region.style.cssText = 'position:fixed;left:0;top:0;width:400px;height:300px;overflow:auto;z-index:1;';
                const tall = document.createElement('div');
                tall.style.height = '2000px';
                region.appendChild(tall);
                document.body.appendChild(region);
            }");

            var buttons = page.Locator(".scroll-buttons").First;
            var topButton = page.Locator(".scroll-button.scroll-to-top").First;
            var bottomButton = page.Locator(".scroll-button.scroll-to-bottom").First;

            // Overflow (2000 - 300 = 1700px) is well past the 240px activation threshold.
            await Assertions.Expect(buttons).ToHaveClassAsync(new Regex(@"\bis-active\b"));
            // Resting at the top: only the scroll-to-bottom button is offered.
            await Assertions.Expect(bottomButton).ToHaveClassAsync(new Regex(@"\bis-visible\b"));
            await Assertions.Expect(topButton).Not.ToHaveClassAsync(new Regex(@"\bis-visible\b"));

            await bottomButton.ClickAsync();

            // Clicking jumps the region toward the bottom (smooth scroll; poll for scrollTop to move
            // well past the edge threshold).
            await page.WaitForFunctionAsync(
                "() => { const r = document.getElementById('synthetic-scroll-region'); return !!r && r.scrollTop > 500; }")
                .DefaultTimeout();

            // Now near the bottom the affordance swaps: scroll-to-top appears.
            await Assertions.Expect(topButton).ToHaveClassAsync(new Regex(@"\bis-visible\b"));
        });
    }

    private static async Task GoToResourcesAndWaitAsync(IPage page)
    {
        await page.GotoAsync("/");
        await Assertions
            .Expect(page.GetByText(InteractionsDashboardServerFixture.ParentResourceName).First)
            .ToBeVisibleAsync();
    }

    public sealed class InteractionsDashboardServerFixture : DashboardServerFixture
    {
        public const string ParentResourceName = "parentapp";
        public const string ChildResourceName = "childdb";

        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: ParentResourceName,
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#parent-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ]),
            // Nested under the parent via the ParentName property; the grid renders it one level
            // deeper (name-cell margin-left = depth * 16px), which is what the parent-hover highlight
            // reads to find descendants.
            ModelTestHelpers.CreateResource(
                resourceName: ChildResourceName,
                resourceType: KnownResourceTypes.Container,
                state: KnownResourceState.Running,
                properties: new Dictionary<string, ResourcePropertyViewModel>
                {
                    [KnownProperties.Resource.ParentName] = new ResourcePropertyViewModel(
                        KnownProperties.Resource.ParentName,
                        new Value { StringValue = ParentResourceName },
                        isValueSensitive: false,
                        knownProperty: null,
                        sortOrder: 0,
                        displayName: null,
                        isHighlighted: false)
                }),
        ];
    }
}
