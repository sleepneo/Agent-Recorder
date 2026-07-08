using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using Xunit;

namespace AgentRecorder.Tests;

public class RegionSelectionFormTests
{
    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);
        return result;
    }

    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);
    }

    [Fact]
    public void InitialBounds_InsideClientArea_EnablesConfirmButton()
    {
        RunOnSta(() =>
        {
            // Virtual screen starts at (0,0); form bounds match virtual screen.
            using var form = new RegionSelectionForm(new Rectangle(100, 150, 800, 600));
            form.Show();
            form.Shown += (_, _) => { };

            // Trigger OnShown logic explicitly to ensure ApplyInitialSelection runs.
            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.True(acceptButton!.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void InitialBounds_OutsideClientArea_Ignored()
    {
        RunOnSta(() =>
        {
            // Bounds far outside the form's client area should be ignored.
            using var form = new RegionSelectionForm(new Rectangle(10000, 10000, 800, 600));
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.False(acceptButton!.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void InitialBounds_TooSmall_Ignored()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm(new Rectangle(100, 100, 10, 10));
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.False(acceptButton!.Enabled);

            form.Close();
        });
    }
}
