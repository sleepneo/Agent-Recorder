using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class RegionSelectionVisualFormTests
{
    [Fact]
    public void SelectionTag_TracksWindowPickAndUsesCachedDisplaySnapshot()
    {
        int displayEnumerationCount = 0;
        var virtualScreen = SystemInformation.VirtualScreen;
        SystemQuery.SetDisplayProvider(() =>
        {
            displayEnumerationCount++;
            return new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Primary", true,
                    new SystemQuery.Bounds(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height), 1.0)
            };
        });

        try
        {
            RunOnSta(() =>
            {
                using var form = new RegionSelectionForm(new Rectangle(
                    virtualScreen.X + 120,
                    virtualScreen.Y + 220,
                    640,
                    480));
                form.Show();

                Assert.Equal("640×480 @ display_1", form.SelectionLabelTextForTests);
                Assert.True(form.SelectionLabelLayoutForTests.IsVisible);
                Assert.True(form.ClientRectangle.Contains(form.SelectionLabelLayoutForTests.Bounds));

                form.ApplyWindowPickForTest(new Rectangle(600, 300, 800, 600));

                Assert.Equal("800×600 @ display_1", form.SelectionLabelTextForTests);
                Assert.True(form.SelectionLabelLayoutForTests.IsVisible);
                Assert.True(form.ClientRectangle.Contains(form.SelectionLabelLayoutForTests.Bounds));
                Assert.Equal(1, displayEnumerationCount);
            });
        }
        finally
        {
            SystemQuery.SetDisplayProvider(null);
        }
    }

    [Fact]
    public void SelectionTag_DoesNotCoverControlPanelOrActionButtonsWhenPlacementIsPossible()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm(new Rectangle(300, 240, 640, 480));
            form.Show();

            var layout = form.SelectionLabelLayoutForTests;
            Assert.True(layout.IsVisible);
            Assert.False(layout.Bounds.IntersectsWith(form.ControlPanelBoundsForTests));
            Assert.False(layout.Bounds.IntersectsWith(form.ConfirmButtonBoundsForTests));
            Assert.False(layout.Bounds.IntersectsWith(form.CancelButtonBoundsForTests));
        });
    }

    [Fact]
    public void SelectionAndDisplayLabelFontsUsePhysicalPixelUnitsWithMatchingSpecs()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm(new Rectangle(300, 240, 640, 480));
            form.Show();

            var selectionFont = form.SelectionLabelFontSpecForTests;
            var displayFont = form.DisplayBoundaryLabelFontSpecForTests;
            Assert.Equal(GraphicsUnit.Pixel, selectionFont.Unit);
            Assert.Equal(GraphicsUnit.Pixel, displayFont.Unit);
            Assert.Equal(form.SelectionVisualMetricsForTests.SelectionLabelFontPixelSize, selectionFont.Size);
            Assert.Equal(form.SelectionVisualMetricsForTests.DisplayBoundaryLabelFontPixelSize, displayFont.Size);
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new InvalidOperationException("STA visual form test failed.", error);
    }
}
