using System.Runtime.InteropServices;

namespace Dictee;

/// <summary>Pastes text into the focused app through the clipboard, then puts the previous clipboard back.</summary>
internal static class Paster
{
    const int VK_CONTROL = 0x11, VK_V = 0x56;

    public static async Task PasteAsync(string text)
    {
        var backup = ClipboardBackup.Capture();
        try
        {
            Clipboard.SetDataObject(text, true, 10, 30);
        }
        catch (ExternalException)
        {
            throw new DictationException("Presse-papier occupé");
        }

        InputSender.Send(new KeyStroke(VK_CONTROL, false), new KeyStroke(VK_V, false), new KeyStroke(VK_V, true), new KeyStroke(VK_CONTROL, true));

        // Leave the target app time to read the clipboard before restoring it.
        await Task.Delay(450);
        backup.Restore();
    }

    /// <summary>Copies every clipboard format (rich text, Excel cells, images, files...) so it can be put back as-is.</summary>
    sealed class ClipboardBackup
    {
        DataObject? data;

        public static ClipboardBackup Capture()
        {
            var backup = new ClipboardBackup();
            try
            {
                if (Clipboard.GetDataObject() is not { } source) return backup;
                var copy = new DataObject();
                foreach (var format in source.GetFormats(false))
                {
                    try
                    {
                        if (source.GetData(format, false) is { } value) copy.SetData(format, false, value);
                    }
                    catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException)
                    {
                        // Some formats can't be read back (delayed rendering); skip them.
                    }
                }
                if (copy.GetFormats().Length > 0) backup.data = copy;
            }
            catch (ExternalException)
            {
            }
            return backup;
        }

        public void Restore()
        {
            try
            {
                if (data != null) Clipboard.SetDataObject(data, true, 10, 30);
                else Clipboard.Clear();
            }
            catch (ExternalException)
            {
            }
        }
    }
}
