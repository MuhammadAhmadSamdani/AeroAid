using System;
using System.Drawing;
using System.Windows.Forms;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Telemetry;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Lightweight map using the same provider/cache as Mission Planner's Flight Data map.</summary>
    public class VehicleMap : GMapControl
    {
        private readonly GMapOverlay overlay = new GMapOverlay("aeroaid-vehicle");
        private readonly VehicleMarker marker = new VehicleMarker(new PointLatLng(0, 0));
        private bool centeredOnce;

        public VehicleMap()
        {
            try
            {
                MapProvider = GCSViews.FlightData.mymap?.MapProvider ?? GMapProviders.GoogleSatelliteMap;
            }
            catch (Exception)
            {
                MapProvider = GMapProviders.GoogleSatelliteMap;
            }

            MinZoom = 2;
            MaxZoom = 22;
            Zoom = 3;
            DragButton = MouseButtons.Left;
            ShowCenter = false;
            CanDragMap = true;
            MarkersEnabled = true;
            BackColor = BrandingManager.Palette.Background;
            marker.IsVisible = false;
            overlay.Markers.Add(marker);
            Overlays.Add(overlay);
        }

        /// <summary>When true the map keeps the vehicle centred.</summary>
        public bool FollowVehicle { get; set; } = true;

        public void UpdateVehicle(TelemetrySnapshot t)
        {
            if (!t.HasHeartbeat || !t.HasPosition)
            {
                if (marker.IsVisible)
                {
                    marker.IsVisible = false;
                    Invalidate();
                }

                return;
            }

            var pos = new PointLatLng(t.Lat, t.Lng);
            marker.Position = pos;
            marker.Heading = (float)t.Heading;
            marker.Armed = t.Armed;
            marker.IsVisible = true;
            if (!centeredOnce)
            {
                Zoom = 17;
                Position = pos;
                centeredOnce = true;
            }
            else if (FollowVehicle)
            {
                Position = pos;
            }

            Invalidate();
        }

        private class VehicleMarker : GMapMarker
        {
            private static readonly PointF[] Arrow =
            {
                new PointF(0, -16), new PointF(11, 12), new PointF(0, 6), new PointF(-11, 12)
            };

            public float Heading;
            public bool Armed;

            public VehicleMarker(PointLatLng p) : base(p)
            {
                Size = new Size(32, 32);
                Offset = new Point(0, 0);
            }

            public override void OnRender(IGraphics g)
            {
                if (Math.Abs(LocalPosition.X) > 100000 || Math.Abs(LocalPosition.Y) > 100000)
                    return;
                var old = g.Transform;
                g.TranslateTransform(LocalPosition.X, LocalPosition.Y);
                g.RotateTransform(Heading - Overlay.Control.Bearing);
                var p = BrandingManager.Palette;
                using (var halo = new SolidBrush(Color.FromArgb(70, Armed ? p.StatusCritical : p.Accent)))
                    g.FillEllipse(halo, -20, -20, 40, 40);
                using (var b = new SolidBrush(p.Accent))
                    g.FillPolygon(b, Arrow);
                using (var pen = new Pen(p.Background, 2f))
                    g.DrawPolygon(pen, Arrow);
                g.Transform = old;
            }
        }
    }
}
