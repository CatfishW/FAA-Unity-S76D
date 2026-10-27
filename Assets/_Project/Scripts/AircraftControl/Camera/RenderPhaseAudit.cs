using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace AircraftControl.Camera
{
    /// <summary>Optional local breadcrumbs for diagnosing native crashes inside before-render callbacks.</summary>
    public static class RenderPhaseAudit
    {
        private static readonly bool enabled = Array.Exists(Environment.GetCommandLineArgs(),
            value => string.Equals(value, "--xr3-audit", StringComparison.OrdinalIgnoreCase));
        private static FileStream output;
        public static void Mark(string phase)
        {
            if (!enabled || Application.platform != RuntimePlatform.WindowsPlayer) return;
            try
            {
                if (output == null)
                    output = new FileStream(Path.Combine(Application.persistentDataPath, "XR3-before-render.txt"),
                        FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 256);
                byte[] text = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O") + " frame=" + Time.frameCount + " " + phase + "\n");
                output.Position = 0; output.Write(text, 0, text.Length); output.SetLength(text.Length); output.Flush();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
