using QQAlchemyDesktop.Automation;

namespace QQAlchemyDesktop.Tests;

public sealed class WindowsCaptureIntegrationTests
{
    [Fact]
    public async Task VisibleWindow_CanBeCapturedByWindowsGraphicsCaptureAndReadByOcr()
    {
        var ready = new TaskCompletionSource<(IntPtr Handle, Form Form)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var form = new Form
            {
                Text = "QQAlchemyCaptureTest", Width = 420, Height = 260,
                StartPosition = FormStartPosition.Manual, Location = new Point(30, 30), ShowInTaskbar = false
            };
            form.Controls.Add(new Label { Text = "药材背包", AutoSize = true, Location = new Point(80, 80), Font = new Font("Microsoft YaHei UI", 20) });
            form.Shown += (_, _) => ready.TrySetResult((form.Handle, form));
            System.Windows.Forms.Application.Run(form);
            form.Dispose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var (handle, form) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var capture = new WindowsGraphicsCaptureService();
            using var bitmap = await capture.CaptureAsync(handle);
            Assert.True(bitmap.Width > 100);
            Assert.True(bitmap.Height > 100);
            var observation = await new RapidOcrService().RecognizeAsync(bitmap);
            Assert.Equal(64, observation.FrameHash.Length);
            Assert.Contains("药材背包", observation.RawText);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
