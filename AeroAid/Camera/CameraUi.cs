using System;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>UI helpers shared by the camera screens.</summary>
    public static class CameraUi
    {
        /// <summary>Starts the stream, offering Mission Planner's GStreamer installer if it is missing.</summary>
        public static void StartStream()
        {
            if (!CameraSettings.IsConfigured)
            {
                CustomMessageBox.Show("Enter the camera IP address (or RTSP URL) in Settings › Camera first.", BrandingManager.AppName);
                return;
            }

            if (!VideoStreamManager.GStreamerAvailable())
            {
                if (CustomMessageBox.Show("Live video requires GStreamer (the video engine used by Mission Planner).\n" +
                                          "Download and install it now (~120 MB from firmware.ardupilot.org)?",
                        BrandingManager.AppName, MessageBoxButtons.YesNo) != (int)DialogResult.Yes)
                    return;
                GStreamerUI.DownloadGStreamer();
                if (!VideoStreamManager.GStreamerAvailable())
                {
                    CustomMessageBox.Show("GStreamer could not be installed. You can install the MinGW 64-bit runtime manually from " +
                                          "https://gstreamer.freedesktop.org/download/", BrandingManager.AppName);
                    return;
                }
            }

            VideoStreamManager.Instance.Start();
        }

        public static string SnapshotDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), BrandingManager.AppName, "Snapshots");

        /// <summary>Saves the current frame as PNG; returns the path or null.</summary>
        public static string SaveSnapshot()
        {
            var frame = VideoStreamManager.Instance.GetFrameCopy(out _);
            if (frame == null)
            {
                CustomMessageBox.Show("No video frame available.", BrandingManager.AppName);
                return null;
            }

            try
            {
                Directory.CreateDirectory(SnapshotDirectory);
                var path = Path.Combine(SnapshotDirectory, "snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                using (frame)
                    frame.Save(path, ImageFormat.Png);
                return path;
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Saving snapshot failed: " + ex.Message, BrandingManager.AppName);
                return null;
            }
        }
    }
}
