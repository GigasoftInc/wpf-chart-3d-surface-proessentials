using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;
using Gigasoft.ProEssentials.EventArg;

namespace GigaPrime3D
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public HeightMap CurrentHeightMap { get; set; }
        public HeightMap HeightMapA { get; set; }

        private bool _bShowingPlane = false;

        private int _nDataStep = 2;    // Reduce Data default as user may have a slow PC without dedicated GPU 
        private int _nAppliedStep = 2; // if less than 1000x1000 we avoid reduction. 
                                       // With the chart magnified and cursor tracking enabled, look for the red quad under the mouse.
                                       // When showing full data, and cursor tracking, the hit testing is a time intensive cpu process and you will see
                                       // a diiference in performace of hit testing when showing full data above 2000x2000. 
                                       // You may also see a delay in hit testing being enabled.  When the chart is reset and or hit testing enabled, the
                                       // chart creates a secondary worker thread to build the octree data structure and it may take a half second for
                                       // this thread to complete on large 3000x3000 surfaces.   
        private int _rows;
        private int _cols;
        private bool _bZoomed = false;

        private float _maxx = 0f;
        private float _miny = 0f;
        private float _maxy = 0f;
        private float _minz = 0f;
        private float _maxz = 0f;

        private float _VertLightDegree = 0f;
        private float _HorzLightDegree = 0f;

        private int CursorTrackingMenu3d = 0;
        private int UndoZoomMenu3d = 1;

        // 36M points x 4 bytes ~150M 
        // Passing shared app memory to both Pe3do and Pesgo saves 150M
        // Always best to save memory where we can 
        // Set up static shared memory resources
        private static float[] sMyXData = new float[6000];  // cols max
        private static float[] sMyZData = new float[6000];  // rows max
        private static float[] sMyYData = new float[36000000];  // rows x cols max

        private GCHandle _pinXHandle;  // data less than 85K bytes, best to pin
        private GCHandle _pinZHandle;  // no need to pin YData > 85K

        public MainWindow()
        {
            // being safe, pin the smaller data arrays x and z
            _pinXHandle = GCHandle.Alloc(sMyXData, GCHandleType.Pinned);
            _pinZHandle = GCHandle.Alloc(sMyZData, GCHandleType.Pinned);

            InitializeComponent();
            Chart3DSurface.Loaded += Chart_Loaded;
            Chart3DSurface.MouseMove += Chart_MouseMove;
            Chart3DSurface.PeCustomMenu += new Pe3doWpf.CustomMenuEventHandler(Chart_PeCustomMenu);

            Chart2DContour.MouseEnter += Chart2DContour_MouseEnter;
            Chart2DContour.Loaded += Chart2DContour_Loaded;
            Chart2DContour.PeZoomIn += Chart2DContour_OnPeZoomIn;
            Chart2DContour.PeZoomOut += Chart2DContour_OnPeZoomOut;
            Chart2DContour.PeHorzScroll += Chart2DContour_PeHorzScroll;
            Chart2DContour.PeVertScroll += Chart2DContour_PeVertScroll;

            Chart2DLine.Loaded += Chart2D_Loaded;
        }

        ~MainWindow()
        {
            // unpin the smaller data arrays //
            if (_pinXHandle.IsAllocated) { _pinXHandle.Free(); }
            if (_pinZHandle.IsAllocated) { _pinZHandle.Free(); }
        }

        // Main 3D Chart showing material surface data and or terrain height data 
        // Main 3D Chart  Pe3doWpf control_Loaded Event 
        void Chart_Loaded(object sender, RoutedEventArgs e)
        {
            Initialize3D();

            HeightMaps.SelectedIndex = 0; // Set default chart example file;  // invokes initial RefreshUi(hm);

            SliderHorizontalRotation.Value = Chart3DSurface.PeUserInterface.Scrollbar.DegreeOfRotation;
            SliderVerticalRotation.Value = Chart3DSurface.PeUserInterface.Scrollbar.ViewingHeight;
            SliderZoom.Value = Chart3DSurface.PePlot.Option.DxZoom;

            _updatingUi = true;
            SliderVerticalLightRotation.Value = 300f;
            SliderHorizontalLightRotation.Value = 180f;
            SliderHorizontalMove.Value = 0f;
            SliderVerticalMove.Value = 2.5f;
            _updatingUi = false;

        }

        // Left Side 2D Chart Contour chart showing same material surface data and or terrain height data 
        // Zooming/Panning the 2D Contour chart will zoom the 3D chart by setting ManualMinX ManualMaxX  etc  
        // 2D Contour Chart  PesgoWpf control_Loaded Event 
        private void Chart2DContour_Loaded(object sender, RoutedEventArgs e)
        {
            Initialize2DContour();
        }

        private void Chart2D_Loaded(object sender, RoutedEventArgs e)
        {
            Initialize2D();
        }

        private void Initialize3D()
        {
            if (Chart3DSurface.Chart == null) { return; }  // Sanity check to make sure Pe3doWpf is loaded  

            try
            {
                // always start a 3D new initialization with a call to Reset
                Chart3DSurface.PeFunction.Reset();

                Chart3DSurface.PeSpecial.AutoImageReset = false; // important for final optimization 

                Chart3DSurface.IsManipulationEnabled = true;

                Chart3DSurface.PeString.MainTitle = string.Empty;
                Chart3DSurface.PeString.SubTitle = string.Empty;
                Chart3DSurface.PeString.MultiSubTitles[0] = string.Empty;
                Chart3DSurface.PeString.XAxisLabel = "X";
                Chart3DSurface.PeString.YAxisLabel = "Z";
                Chart3DSurface.PeString.ZAxisLabel = "Y";

                Chart3DSurface.PeUserInterface.Dialog.ModelessAutoClose = true;  // recommended, if any modeless dialogs are invoked, they will auto close clicking outside them   
                Chart3DSurface.PeUserInterface.Dialog.PlotCustomization = true;  // to allow both ColorContour and monochrome ShadedContour

                Chart3DSurface.PePlot.PolyMode = PolyMode.SurfacePolygons;      // Surface, vs Scatter or Bar or Area(waterfall) 
                Chart3DSurface.PePlot.Method = ThreeDGraphPlottingMethod.Four;  // Surface  
                Chart3DSurface.PeColor.DxTransparencyMode = TransparencyMode.None;  // Setting OIT (order independent transparency) will hurt performance, use with many complex translucent graph annotations 

                Chart3DSurface.PeGrid.Configure.DxPsManualCullXZ = true; // Enable Pixel shader culling as zooming the 2D contour will zoom the 3D chart to match  
                Chart3DSurface.PePlot.Option.DxFitControlShape = false;  // almost always false as it allows control of aspect ratio for x y z axes 
                Chart3DSurface.PePlot.Option.DxViewportX = 0;     // controls / tweaks position of surface within the chart  
                Chart3DSurface.PePlot.Option.DxViewportY = 2.5F;  // controls / tweaks position of surface within the chart  
                Chart3DSurface.PePlot.Option.DxFOV = 1;
                Chart3DSurface.PePlot.Option.DxZoom = -24.0f;     // control visual distance to surface 
                Chart3DSurface.PeUserInterface.Scrollbar.ViewingHeight = 28;
                Chart3DSurface.PeUserInterface.Scrollbar.DegreeOfRotation = 145;

                Chart3DSurface.PePlot.Option.DegreePrompting = true;   // top left numbers showing rotation, distance, light location stats   
                Chart3DSurface.PePlot.LinesOrTubes = LinesOrTubes.AllLines;  // lines are less complex and always bright.  tubes are more complex and also lighted as other triangles 
                Chart3DSurface.PePlot.SubsetLineTypes[0] = LineType.MediumThinSolid;

                Chart3DSurface.PePlot.Allow.WireFrame = false;
                Chart3DSurface.PePlot.Option.SurfacePolygonBorders = true;
                Chart3DSurface.PePlot.Option.ShowContour = ShowContour.None;

                Chart3DSurface.PeFont.SizeGlobalCntl = 1.1f;
                Chart3DSurface.PeFont.Fixed = true;
                Chart3DSurface.PeFont.FontSize = Gigasoft.ProEssentials.Enums.FontSize.Large;

                Chart3DSurface.PeAnnotation.Show = true;

                // Helps add a bit of padding because PixelShader culling may cull data at very edge of chart when we prefer to see this data 
                Chart3DSurface.PeGrid.Configure.AutoPadBeyondZeroX = true;
                Chart3DSurface.PeGrid.Configure.AutoPadBeyondZeroY = true;
                Chart3DSurface.PeGrid.Configure.AutoPadBeyondZeroZ = true;
                Chart3DSurface.PeGrid.Configure.AutoMinMaxPaddingX = 1;
                Chart3DSurface.PeGrid.Configure.AutoMinMaxPaddingY = 1;
                Chart3DSurface.PeGrid.Configure.AutoMinMaxPaddingZ = 1;

                Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.None;
                Chart3DSurface.PeGrid.Configure.ManualScaleControlY = ManualScaleControl.None;
                Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.None;
                Chart3DSurface.PeGrid.Option.ShowXAxis = ShowAxis.All;
                Chart3DSurface.PeGrid.Option.ShowYAxis = ShowAxis.All;
                Chart3DSurface.PeGrid.Option.ShowZAxis = ShowAxis.All;

                Chart3DSurface.PeUserInterface.RotationDetail = RotationDetail.WireFrame;
                Chart3DSurface.PeUserInterface.Allow.FocalRect = false;
                Chart3DSurface.PeUserInterface.Menu.LegendLocation = MenuControl.Hide;

                Chart3DSurface.PeUserInterface.Scrollbar.ScrollSmoothness = 0;
                Chart3DSurface.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness = 2;
                Chart3DSurface.PeUserInterface.Scrollbar.MouseWheelZoomFactor = 25.0f;
                Chart3DSurface.PeUserInterface.Scrollbar.PinchZoomSmoothness = 2;
                Chart3DSurface.PeUserInterface.Scrollbar.PinchZoomFactor = 20.0f;

                Chart3DSurface.PePlot.Option.DxViewportPanFactor = 10.0F; // New feature to improve SHIFT plus CLICK DRAG to translate scene 
                Chart3DSurface.PePlot.Option.DxZoomMin = -40; // control how far user can zoom in and out  
                Chart3DSurface.PePlot.Option.DxZoomMax = -3;

                Chart3DSurface.PeUserInterface.Scrollbar.HorzScrollBar = false; // hides scrollbars, user can click drag the chart
                Chart3DSurface.PeUserInterface.Scrollbar.VertScrollBar = false; // hides scrollbars, user can click drag the chart
                Chart3DSurface.PeUserInterface.Scrollbar.MouseDraggingX = true;
                Chart3DSurface.PeUserInterface.Scrollbar.MouseDraggingY = true;
                Chart3DSurface.PeUserInterface.Scrollbar.MouseWheelFunction = MouseWheelFunction.HorizontalVerticalZoom;
                Chart3DSurface.PeUserInterface.Scrollbar.MouseWheelZoomEvents = true;

                Chart3DSurface.PeData.DuplicateDataX = DuplicateData.PointIncrement;  // probably best to set when passing data 
                Chart3DSurface.PeData.DuplicateDataZ = DuplicateData.SubsetIncrement; // but this says XData and ZData only contain data for one row / column 

                // Controls color of Surface //
                Chart3DSurface.PeColor.SubsetColors.Clear();
                Chart3DSurface.PeColor.SubsetShades.Clear();
                for (var colx = 0; colx < 256; ++colx) // change 256 to 100 to use 100 closest colors 
                {
                    var index = (byte)(colx / 256.0 * (MyColors.Length - 1));  // change 256 to 100 to use 100 closest colors 
                    var color = MyColors[index];
                    Chart3DSurface.PeColor.SubsetColors[colx] = color;
                    Chart3DSurface.PeColor.SubsetShades[colx] = Color.FromArgb(255, (byte)(100 + colx), (byte)(100 + colx), (byte)(100 + colx));
                }
                Chart3DSurface.PeColor.SubsetColors[(int)Gigasoft.ProEssentials.Enums.SurfaceColors.SolidSurface] = Color.FromArgb(255, 170, 170, 255);

                // non data Color settings //
                Chart3DSurface.PeColor.BitmapGradientMode = false;
                Chart3DSurface.PeColor.QuickStyle = QuickStyle.DarkNoBorder; // always set above bitmap gradient mode before quickstyle 
                Chart3DSurface.PeConfigure.BorderTypes = TABorder.NoBorder;
                Chart3DSurface.PeColor.GraphBmpStyle = BitmapStyle.NoBmp;
                Chart3DSurface.PeColor.GraphBackground = Color.FromRgb(0x00, 0x2B, 0x35);
                Chart3DSurface.PeColor.Desk = Color.FromRgb(0x35, 0x2B, 0x00); // RGB is BGR for DeskColor, don't want to break code so leaving it
                Chart3DSurface.PeColor.GraphForeground = System.Windows.Media.Colors.White;
                Chart3DSurface.PeColor.ZAxis = System.Windows.Media.Colors.White;
                Chart3DSurface.PeColor.YAxis = System.Windows.Media.Colors.White;
                Chart3DSurface.PeColor.XAxis = System.Windows.Media.Colors.White;
                Chart3DSurface.PeColor.Text = System.Windows.Media.Colors.White;

                Chart3DSurface.PeLegend.ContourStyle = true;
                Chart3DSurface.PeLegend.Show = false; // there will be a UI CheckBox that controls this  
                Chart3DSurface.PeLegend.Location = LegendLocation.Right;

                Chart3DSurface.PeData.NullDataValue = -9999;
                Chart3DSurface.PeData.NullDataValueX = -9999;
                Chart3DSurface.PeData.NullDataValueZ = -9999;

                Chart3DSurface.PeUserInterface.Cursor.PromptLocation = CursorPromptLocation.ToolTip;

                Chart3DSurface.PeLegend.ContourLegendPrecision = ContourLegendPrecision.TwoDecimals;

                Chart3DSurface.PeFont.SizeGlobalCntl = 1.35F;

                Chart3DSurface.PeUserInterface.Allow.Customization = false;
                Chart3DSurface.PeUserInterface.Allow.Maximization = false;
                Chart3DSurface.PeUserInterface.Menu.Contour = MenuControl.Hide;  // Add a UI to enable bottom contours  
                Chart3DSurface.PeUserInterface.Menu.BorderType = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.BitmapGradient = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.ShowLegend = MenuControl.Hide;
                //Chart3DSurface.PeUserInterface.Menu.PlotMethod = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.CustomizeDialog = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.DataShadow = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.QuickStyle = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.DataPrecision = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.Rotation = MenuControl.Hide;
                Chart3DSurface.PeUserInterface.Menu.LegendLocation = MenuControl.Hide;  // add a UI to place on bottom 

                Chart3DSurface.PeUserInterface.Dialog.AllowEmfExport = false;
                Chart3DSurface.PeUserInterface.Dialog.AllowSvgExport = false;
                Chart3DSurface.PeUserInterface.Dialog.AllowWmfExport = false;
                Chart3DSurface.PeUserInterface.Allow.TextExport = false;
                Chart3DSurface.PeUserInterface.Dialog.HideExportImageDpi = true;
                Chart3DSurface.PeUserInterface.Dialog.HidePrintDpi = true;
                Chart3DSurface.PeUserInterface.Allow.FocalRect = false;

                Chart3DSurface.PeUserInterface.Menu.CustomMenuText[CursorTrackingMenu3d] = "Cursor Tracking";
                Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0] = CustomMenuState.UnChecked;
                Chart3DSurface.PeUserInterface.Menu.CustomMenuLocation[CursorTrackingMenu3d] = CustomMenuLocation.Bottom;

                Chart3DSurface.PeUserInterface.Menu.CustomMenuText[UndoZoomMenu3d] = "Undo Zoom";
                Chart3DSurface.PeUserInterface.Menu.CustomMenuLocation[UndoZoomMenu3d] = CustomMenuLocation.Bottom;
                Chart3DSurface.PeUserInterface.Menu.CustomMenu[UndoZoomMenu3d, 0] = CustomMenu.Grayed;

                Chart3DSurface.PeConfigure.RenderEngine = RenderEngine.Direct3D;
                Chart3DSurface.PeConfigure.PrepareImages = true;
                Chart3DSurface.PeConfigure.CacheBmp = true;
                Chart3DSurface.PeConfigure.AntiAliasGraphics = false;
                Chart3DSurface.PeConfigure.AntiAliasText = false;

                Chart3DSurface.PeFunction.SetViewingAt(0.0f, 0.0f, 0.0f);  // default, not really needed here 

                // Final settings to control how scene is asked to render / refresh  
                Chart3DSurface.PeFunction.Force3dxNewColors = true;
                Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
                Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
                Chart3DSurface.PeFunction.ReinitializeResetImage();
                Chart3DSurface.Invalidate();

                Chart3DSurface.UpdateLayout();

            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }

        }

        private void Initialize2DContour()  // Left side PesgoWpf 2d contour 
        {
            if (Chart2DContour.Chart == null) { return; }  // Sanity check to make sure Pe3doWpf is loaded  

            Chart2DContour.PeConfigure.RenderEngine = RenderEngine.Direct3D;
            Chart2DContour.PeConfigure.Composite2D3D = Composite2D3D.Foreground;

            Chart2DContour.PeUserInterface.Allow.Zooming = AllowZooming.HorzAndVert;
            Chart2DContour.PeUserInterface.Allow.ZoomStyle = ZoomStyle.Ro2Not;

            Chart2DContour.PePlot.Allow.ContourColors = true;
            Chart2DContour.PePlot.Allow.ContourColorsShadows = true;
            Chart2DContour.PePlot.Allow.ContourLines = false;

            Chart2DContour.PeConfigure.PrepareImages = true;
            Chart2DContour.PeConfigure.CacheBmp = true;
            Chart2DContour.PeConfigure.AntiAliasGraphics = true;
            Chart2DContour.PePlot.SubsetLineTypes[0] = LineType.MediumSolid;

            Chart2DContour.PeUserInterface.Allow.FocalRect = false;
            Chart2DContour.Focusable = false; // false;

            Chart2DContour.PeColor.Desk = Color.FromRgb(0x00, 0x2B, 0x35);

            Chart2DContour.PeColor.Text = Color.FromRgb(255, 255, 255);
            Chart2DContour.PeColor.GraphBackground = Color.FromRgb(0x00, 0x2B, 0x35);

            Chart2DContour.PeConfigure.BorderTypes = TABorder.NoBorder;

            Chart2DContour.PeString.MainTitle = "";
            Chart2DContour.PeString.SubTitle = "";
            Chart2DContour.PeString.YAxisLabel = "";
            Chart2DContour.PeString.XAxisLabel = "";

            Chart2DContour.PeGrid.Configure.AutoMinMaxPadding = 0;

            Chart2DContour.PeLegend.ContourLegendPrecision = ContourLegendPrecision.TwoDecimals;
            Chart2DContour.PeLegend.ContourStyle = true;
            Chart2DContour.PeLegend.Location = LegendLocation.Left;
            Chart2DContour.PeLegend.Show = false;


            Chart2DContour.PeColor.SubsetColors.Clear();
            Chart2DContour.PeColor.SubsetShades.Clear();
            for (var colx = 0; colx < 256; ++colx)   // change 256 to 100 to use 100 closest colors 
            {
                var index = (byte)(colx / 256.0 * (MyColors.Length - 1)); // change 256 to 100 to use 100 closest colors  
                var color = MyColors[index];
                Chart2DContour.PeColor.SubsetColors[colx] = color;
                Chart2DContour.PeColor.SubsetShades[colx] = Color.FromArgb(255, (byte)(100 + colx), (byte)(100 + colx), (byte)(100 + colx));
            }

            Chart2DContour.PeConfigure.ImageAdjustLeft = -100; // shrink-tweak the borders
            Chart2DContour.PeConfigure.ImageAdjustRight = -100;
            Chart2DContour.PeConfigure.ImageAdjustTop = 50;
            Chart2DContour.PeConfigure.ImageAdjustBottom = 0;

            Chart2DContour.PeUserInterface.Scrollbar.ScrollingHorzZoom = true;
            Chart2DContour.PeUserInterface.Scrollbar.ScrollingVertZoom = true;

            Chart2DContour.PeConfigure.Composite2D3D = Composite2D3D.Foreground;
            Chart2DContour.PeConfigure.RenderEngine = RenderEngine.Direct3D;

            Chart2DContour.PeUserInterface.Allow.Customization = false;
            Chart2DContour.PeUserInterface.Allow.Maximization = false;
            Chart2DContour.PeUserInterface.Menu.BorderType = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.BitmapGradient = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.ShowLegend = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.PlotMethod = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.CustomizeDialog = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.DataShadow = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.QuickStyle = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.DataPrecision = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.LegendLocation = MenuControl.Hide;  // add a UI to place on bottom 
            Chart2DContour.PeUserInterface.Menu.MarkDataPoints = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.ViewingStyle = MenuControl.Hide;
            Chart2DContour.PeUserInterface.Menu.GridLine = MenuControl.Hide;

            Chart2DContour.PeUserInterface.Dialog.AllowEmfExport = false;
            Chart2DContour.PeUserInterface.Dialog.AllowSvgExport = false;
            Chart2DContour.PeUserInterface.Dialog.AllowWmfExport = false;
            Chart2DContour.PeUserInterface.Allow.TextExport = false;
            Chart2DContour.PeUserInterface.Dialog.HideExportImageDpi = true;
            Chart2DContour.PeUserInterface.Dialog.HidePrintDpi = true;

            Chart2DContour.PeColor.Desk = Color.FromRgb(0x35, 0x2B, 0x00); // there's a bug in the control! Ya, R B reversed.

        }

        private void Initialize2D() // Right side PesgoWpf 2d crosssection plane 
        {
            if (Chart2DLine.Chart == null) { return; }  // Sanity check to make sure Pe3doWpf is loaded  

            Chart2DLine.PeConfigure.RenderEngine = RenderEngine.Direct2D;  // its also the default for WPF 

            Chart2DLine.PeString.MainTitle = "";
            Chart2DLine.PeString.SubTitle = "";
            Chart2DLine.PeString.XAxisLabel = "Z";
            Chart2DLine.PeString.YAxisLabel = "Y";
            Chart2DLine.PeGrid.Option.XAxisVertNumbering = true;

            Chart2DLine.PeFont.SizeGlobalCntl = .90F;

            Chart2DLine.PeConfigure.PrepareImages = true;
            Chart2DLine.PeConfigure.CacheBmp = true;
            Chart2DLine.PeConfigure.AntiAliasGraphics = true;
            Chart2DLine.PePlot.SubsetLineTypes[0] = LineType.MediumSolid;

            Chart2DLine.PeUserInterface.Allow.FocalRect = false;
            Chart2DLine.Focusable = false;

            Chart2DLine.PeColor.Desk = Color.FromRgb(0x00, 0x2B, 0x35); // I think there's a bug in the control!
            Chart2DLine.PeColor.Text = Color.FromRgb(255, 255, 255);
            Chart2DLine.PeColor.GraphBackground = Color.FromRgb(0x00, 0x2B, 0x35);
            Chart2DLine.PeColor.GraphForeground = System.Windows.Media.Colors.White;

            Chart2DLine.PeConfigure.BorderTypes = TABorder.NoBorder;
            Chart2DLine.PeGrid.GridBands = false;

            Chart2DLine.PeUserInterface.Allow.Customization = false;
            Chart2DLine.PeUserInterface.Allow.Maximization = false;
            Chart2DLine.PeUserInterface.Menu.BorderType = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.BitmapGradient = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.ShowLegend = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.PlotMethod = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.CustomizeDialog = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.DataShadow = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.QuickStyle = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.DataPrecision = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.LegendLocation = MenuControl.Hide;  // add a UI to place on bottom 
            Chart2DLine.PeUserInterface.Menu.MarkDataPoints = MenuControl.Hide;
            Chart2DLine.PeUserInterface.Menu.ViewingStyle = MenuControl.Hide;

            Chart2DLine.PeUserInterface.Dialog.AllowEmfExport = false;
            Chart2DLine.PeUserInterface.Dialog.AllowSvgExport = false;
            Chart2DLine.PeUserInterface.Dialog.AllowWmfExport = false;
            Chart2DLine.PeUserInterface.Allow.TextExport = false;
            Chart2DLine.PeUserInterface.Dialog.HideExportImageDpi = true;
            Chart2DLine.PeUserInterface.Dialog.HidePrintDpi = true;

        }


        private void Chart2DContour_MouseEnter(object sender, MouseEventArgs e)
        {
            // Disable Chart Pe3do hot spots (cursor tracking)
            // Panning a zoomed 2D contour over burdens Oct-Tree HotSpot data structure re-creation // 
            CustomMenuState n = Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0];
            if (n == CustomMenuState.Checked || HotSpots.IsChecked == true)
            {
                Chart2DContour.PeAnnotation.Graph.Show = false;  // remmove the dot if there happens to be one 
                Chart2DContour.PeAnnotation.Show = false;
                Chart2DContour.PeFunction.ResetImage(0, 0);
                Chart2DContour.Invalidate();

                n = CustomMenuState.UnChecked;
                Chart3DSurface.Freeze = true;
                Chart3DSurface.PeUserInterface.Cursor.HighlightColor = Color.FromArgb(0, 0, 0, 0);
                HotSpots.IsChecked = false;
                Chart3DSurface.PeUserInterface.HotSpot.Data = false;
                Chart3DSurface.PeUserInterface.Cursor.PromptTracking = false;
                Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0] = n;
                Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
                Chart3DSurface.PeFunction.ReinitializeResetImage();
                Chart3DSurface.Invalidate();
                Chart3DSurface.Freeze = false;
            }
        }

        private void Chart_PeCustomMenu(object sender, Gigasoft.ProEssentials.EventArg.CustomMenuEventArgs e)
        {
            CustomMenuState n = CustomMenuState.UnChecked;

            // Custom Menu was clicked //
            if (e.MenuIndex == CursorTrackingMenu3d) // Cursor Tracking State 
            {
                // Reverse option //
                n = Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0];
                if (n == CustomMenuState.UnChecked)
                {
                    n = CustomMenuState.Checked;
                    HotSpots.IsChecked = true;
                    Chart3DSurface.PeUserInterface.HotSpot.Data = true;
                    Chart3DSurface.PeUserInterface.Cursor.PromptTracking = true;
                    Chart3DSurface.PeUserInterface.Cursor.HighlightColor = Color.FromArgb(255, 255, 0, 0);
                }
                else
                {
                    n = CustomMenuState.UnChecked;
                    HotSpots.IsChecked = false;
                    Chart3DSurface.PeUserInterface.HotSpot.Data = false;
                    Chart3DSurface.PeUserInterface.Cursor.PromptTracking = false;
                    Chart3DSurface.PeUserInterface.Cursor.HighlightColor = Color.FromArgb(0, 0, 0, 0);
                }
                Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0] = n;

                Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
                Chart3DSurface.PeFunction.ReinitializeResetImage();

                Chart3DSurface.Invalidate();
                return;
            }
            if (e.MenuIndex == UndoZoomMenu3d) // Undo Zoom 
            {
                if (_bZoomed)
                {
                    Chart2DContour.PeGrid.Zoom.Mode = false;
                    _bZoomed = false;
                    Chart2DContour.Invalidate();

                    Chart3DSurface.PeUserInterface.Menu.CustomMenu[UndoZoomMenu3d, 0] = CustomMenu.Grayed;

                    Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.None;
                    Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.None;

                    Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
                    Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
                    Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 

                    Chart3DSurface.PeFunction.Reinitialize();
                    Chart3DSurface.Invalidate();

                    float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
                    double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * SliderXPlane.Value / 100.0F);
                    MoveXPlane(dPos);

                }
            }
        }


        private void RefreshUi(HeightMap hm)
        {
            // Changing the data of the chart, mostly maintaining all existing settings and states, such as zoom, rotation, etc. 

            CurrentHeightMap = hm;

            _nAppliedStep = 1;
            if (hm.HeightPx > 1001 || hm.WidthPx > 1001) { _nAppliedStep = _nDataStep; }   // only apply possible step for height maps more than 1000 x 1000

            _rows = hm.HeightPx / _nAppliedStep;
            _cols = hm.WidthPx / _nAppliedStep;

            var size = _rows * _cols;
            float fResolution = (float)hm.Resolution;

            var idx = 0;
            for (var row = 0; row < hm.HeightPx - (_nAppliedStep - 1); row += _nAppliedStep)
                sMyZData[idx++] = row * fResolution;

            idx = 0;
            for (var col = 0; col < hm.WidthPx - (_nAppliedStep - 1); col += _nAppliedStep)
                sMyXData[idx++] = col * fResolution;

            // Important setting when changing data without first calling PeFunction.Reset, or DLL call PEreset  
            Chart3DSurface.PeData.ScaleForYData = 0;  // Reset internal scaling factors as we are simply changing data, YAxis scale may be wrong if we do not include this. 

            if (_nAppliedStep > 1)
            {
                idx = 0;  // showing spoon feeding YData into buffer/array incase one needs to scale or alter the data in some way  
                for (var row = 0; row < hm.HeightPx - (_nAppliedStep - 1); row += _nAppliedStep)
                {
                    for (var col = 0; col < hm.WidthPx - (_nAppliedStep - 1); col += _nAppliedStep)
                    {
                        sMyYData[idx++] = (hm.GetPel(row, col));
                    }
                }
            }
            else
            {
                Array.Copy(hm.ImageData, sMyYData, _rows * _cols); // no stepping necessary, simply copy data     
            }

            _maxx = (float)hm.WidthMm;
            _miny = (float)hm.MinZMm;
            _maxy = (float)hm.MaxZMm;
            _minz = 0.0F;
            _maxz = (float)hm.HeightMm;

            Chart3DSurface.PeData.Subsets = _rows;
            Chart3DSurface.PeData.Points = _cols;

            // These settings say XData and ZData only contain data for one row / column
            Chart3DSurface.PeData.DuplicateDataX = DuplicateData.PointIncrement;
            Chart3DSurface.PeData.DuplicateDataZ = DuplicateData.SubsetIncrement;

            ///////////////////////////////////////////////////////////////////////////
            // v10 new feature  - Enable building the scene on the GPU vs the CPU.   //
            // Without these lines, uses v9 CPU side construction which still builds //
            // a Direct3D scene, but the inital chart takes longer to render.        //
            // v10 is 10x faster.  Faster/better looking than any other chart, swap  //
            // the competition's chart into this project and test for yourself.      //
            ///////////////////////////////////////////////////////////////////////////
            Chart3DSurface.PeData.ComputeShader = true;   // for RenderEngine = Direct3D, Polymode/PlottingMethod Surface

            // No transfer of data, set chart uses the app memory, same memory is used for Pesgo below //
            Chart3DSurface.PeData.X.UseDataAtLocation(sMyXData, _cols);
            Chart3DSurface.PeData.Y.UseDataAtLocation(sMyYData, size);
            Chart3DSurface.PeData.Z.UseDataAtLocation(sMyZData, _rows);

            // instead of by Reference above, this would copy data to the chart to hold its own copy // 
            //Chart3DSurface.PeData.X.FastCopyFrom(sMyXData, _cols);
            //Chart3DSurface.PeData.Y.FastCopyFrom(sMyYData, size);
            //Chart3DSurface.PeData.Z.FastCopyFrom(sMyZData, _rows);

            var width = (float)hm.WidthMm;
            var height = (float)hm.HeightMm;
            var diag = (float)Math.Sqrt(width * width + height * height);

            Chart3DSurface.PeGrid.Option.GridAspectX = width;
            Chart3DSurface.PeGrid.Option.GridAspectZ = height;
            Chart3DSurface.PeGrid.Option.GridAspectY = diag * 0.1f; // Z Axis Expansion

            Chart3DSurface.PeString.MainTitle = hm.Path;

            Chart3DSurface.PeFunction.SetLight(0, -2.0F, -7.0F, -7.0F);  // reset the light location 

            _updatingUi = true;
            SliderHorizontalRotation.Value = Chart3DSurface.PeUserInterface.Scrollbar.SBPos;
            SliderVerticalRotation.Value = Chart3DSurface.PeUserInterface.Scrollbar.ViewingHeight;
            SliderZExaggeration.Value = 10;
            _updatingUi = false;

            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;  // THIS was missing.   I added to get your data to change. 
            Chart3DSurface.PeFunction.ReinitializeResetImage();
            Chart3DSurface.Invalidate();

            ///////////////////////////////
            // Set data for 2D Contour   //
            ///////////////////////////////
            Initialize2DContour();

            // Set some basic properties related to data //

            Chart2DContour.PeConfigure.RenderEngine = RenderEngine.Direct3D;
            Chart2DContour.PeData.Subsets = _rows;
            Chart2DContour.PeData.Points = _cols;

            // Similar to above Pe3doWpf, v10 new feature - Enable building the scene on the GPU vs the CPU.  //
            // 2D contours are work intensive // 
            Chart2DContour.PeData.ComputeShader = true;   // for RenderEngine = Direct3D, PlottingMethod SurfaceContour 

            Chart2DContour.PeData.DuplicateDataX = DuplicateData.PointIncrement;
            Chart2DContour.PeData.DuplicateDataY = DuplicateData.SubsetIncrement;

            // No transfer of data, set chart uses the app memory, same memory used for Pe3do above //
            Chart2DContour.PeData.X.UseDataAtLocation(sMyXData, _cols);
            Chart2DContour.PeData.Z.UseDataAtLocation(sMyYData, size);
            Chart2DContour.PeData.Y.UseDataAtLocation(sMyZData, _rows);

            // instead of by Reference above, this would copy data to the chart to hold its own copy // 
            //Chart2DContour.PeData.X.FastCopyFrom(sMyXData, _cols);
            //Chart2DContour.PeData.Z.FastCopyFrom(sMyYData, size);
            //Chart2DContour.PeData.Y.FastCopyFrom(sMyZData, _rows);

            Chart2DContour.PeGrid.Option.GridAspect = (float)_rows / (float)_cols;

            Chart2DContour.PePlot.Method = SGraphPlottingMethod.ContourColors;

            Chart2DContour.PeConfigure.Composite2D3D = Composite2D3D.Foreground;
            Chart2DContour.PeConfigure.RenderEngine = RenderEngine.Direct3D;
            Chart2DContour.PeFunction.Force3dxNewColors = true;
            Chart2DContour.PeFunction.Force3dxVerticeRebuild = true;

            Chart2DContour.PeFunction.ReinitializeResetImage();
            Chart2DContour.Invalidate();

            UpdateLayout();  // just in case events are piled up, helps guarantee rendering is as expected  
        }

        #region future use 
        //private void Timer1_Tick(object sender, EventArgs e)  // possible future use 
        //{
        //    // Timer Tick   
        //}
        #endregion future use

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            #region future use
            //if (aTimer != null) // possible future use 
            //{
            //    aTimer.Stop();
            //    aTimer = null;
            //}
            //if (Timer1 != null)
            //{
            //    Timer1.Stop();
            //    Timer1 = null;
            //}
            #endregion
        }

        void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            // A simple exercise of hot spots //

            // Show on 2D Contour a Dot as location of mouse cursor over the 3D chart 

            if (HotSpots.IsChecked == true)
            {
                // get last mouse location within control //'
                System.Windows.Point pt = Chart3DSurface.PeUserInterface.Cursor.LastMouseMove;
                int pX = (int)pt.X;
                int pY = (int)pt.Y;

                // Call to fill hot spot data structure with hot spot data at given x and y
                Chart3DSurface.PeFunction.GetHotSpot(pX, pY);
                Gigasoft.ProEssentials.Structs.HotSpotData ds = Chart3DSurface.PeFunction.GetHotSpotData();

                // get ydata value at hot spot //
                if (ds.Type == HotSpotType.DataPoint)
                {
                    int nHighLightSubset = ds.Data1;
                    int nHighLightPoint = ds.Data2;

                    int aCnt = 0;
                    Chart2DContour.PeAnnotation.Graph.X[aCnt] = Chart3DSurface.PeData.X[nHighLightSubset, nHighLightPoint];
                    Chart2DContour.PeAnnotation.Graph.Y[aCnt] = Chart3DSurface.PeData.Z[nHighLightSubset, nHighLightPoint];
                    Chart2DContour.PeAnnotation.Graph.Type[aCnt] = (int)Gigasoft.ProEssentials.Enums.GraphAnnotationType.LargeDotSolid;
                    Chart2DContour.PeAnnotation.Graph.Color[aCnt] = Color.FromArgb(255, 50, 50, 50);
                    aCnt++;

                    Chart2DContour.PeAnnotation.Graph.Show = true;
                    Chart2DContour.PeAnnotation.Show = true;
                    Chart2DContour.PeAnnotation.InFront = true;

                    Chart2DContour.PeFunction.ResetImage(0, 0);
                    Chart2DContour.Invalidate();
                }
            }
        }

        #region future use
        //void Chart_KeyDown(object sender, KeyEventArgs e)  // future possible use
        //{
        //    int nKey = Convert.ToInt16(e.Key);
        //}

        // Future possible use Timer or as needs for an async timer //
        //static void aTimer_Tick(object sender, EventArgs e)
        //{
        //    // System.Windows.Application.Current.MainWindow.Dispatcher.Invoke(DispatcherPriority.Render, new NextPrimeDelegate(UpdateChart));
        //}

        // Called via Invoke via aTimer_Tick, as needs for an async timer //
        //static void UpdateChart()
        //{
        //    // future use 
        //}
        #endregion
        #region Color definitions
        public static readonly Color[] MyColors = {
       Color.FromRgb(0, 0, 131),
       Color.FromRgb(0, 0, 135),
       Color.FromRgb(0, 0, 139),
       Color.FromRgb(0, 0, 143),
       Color.FromRgb(0, 0, 147),
       Color.FromRgb(0, 0, 151),
       Color.FromRgb(0, 0, 155),
       Color.FromRgb(0, 0, 159),
       Color.FromRgb(0, 0, 163),
       Color.FromRgb(0, 0, 167),
       Color.FromRgb(0, 0, 171),
       Color.FromRgb(0, 0, 175),
       Color.FromRgb(0, 0, 179),
       Color.FromRgb(0, 0, 183),
       Color.FromRgb(0, 0, 187),
       Color.FromRgb(0, 0, 191),
       Color.FromRgb(0, 0, 195),
       Color.FromRgb(0, 0, 199),
       Color.FromRgb(0, 0, 203),
       Color.FromRgb(0, 0, 207),
       Color.FromRgb(0, 0, 211),
       Color.FromRgb(0, 0, 215),
       Color.FromRgb(0, 0, 219),
       Color.FromRgb(0, 0, 223),
       Color.FromRgb(0, 0, 227),
       Color.FromRgb(0, 0, 231),
       Color.FromRgb(0, 0, 235),
       Color.FromRgb(0, 0, 239),
       Color.FromRgb(0, 0, 243),
       Color.FromRgb(0, 0, 247),
       Color.FromRgb(0, 0, 251),
       Color.FromRgb(0, 0, 254),
       Color.FromRgb(0, 4, 255),
       Color.FromRgb(0, 8, 255),
       Color.FromRgb(0, 12, 255),
       Color.FromRgb(0, 16, 255),
       Color.FromRgb(0, 20, 255),
       Color.FromRgb(0, 24, 255),
       Color.FromRgb(0, 28, 255),
       Color.FromRgb(0, 32, 255),
       Color.FromRgb(0, 36, 255),
       Color.FromRgb(0, 40, 255),
       Color.FromRgb(0, 44, 255),
       Color.FromRgb(0, 48, 255),
       Color.FromRgb(0, 52, 255),
       Color.FromRgb(0, 56, 255),
       Color.FromRgb(0, 60, 255),
       Color.FromRgb(0, 64, 255),
       Color.FromRgb(0, 68, 255),
       Color.FromRgb(0, 72, 255),
       Color.FromRgb(0, 76, 255),
       Color.FromRgb(0, 80, 255),
       Color.FromRgb(0, 84, 255),
       Color.FromRgb(0, 88, 255),
       Color.FromRgb(0, 92, 255),
       Color.FromRgb(0, 96, 255),
       Color.FromRgb(0, 100, 255),
       Color.FromRgb(0, 104, 255),
       Color.FromRgb(0, 108, 255),
       Color.FromRgb(0, 112, 255),
       Color.FromRgb(0, 116, 255),
       Color.FromRgb(0, 120, 255),
       Color.FromRgb(0, 124, 255),
       Color.FromRgb(0, 128, 255),
       Color.FromRgb(0, 131, 255),
       Color.FromRgb(0, 135, 255),
       Color.FromRgb(0, 139, 255),
       Color.FromRgb(0, 143, 255),
       Color.FromRgb(0, 147, 255),
       Color.FromRgb(0, 151, 255),
       Color.FromRgb(0, 155, 255),
       Color.FromRgb(0, 159, 255),
       Color.FromRgb(0, 163, 255),
       Color.FromRgb(0, 167, 255),
       Color.FromRgb(0, 171, 255),
       Color.FromRgb(0, 175, 255),
       Color.FromRgb(0, 179, 255),
       Color.FromRgb(0, 183, 255),
       Color.FromRgb(0, 187, 255),
       Color.FromRgb(0, 191, 255),
       Color.FromRgb(0, 195, 255),
       Color.FromRgb(0, 199, 255),
       Color.FromRgb(0, 203, 255),
       Color.FromRgb(0, 207, 255),
       Color.FromRgb(0, 211, 255),
       Color.FromRgb(0, 215, 255),
       Color.FromRgb(0, 219, 255),
       Color.FromRgb(0, 223, 255),
       Color.FromRgb(0, 227, 255),
       Color.FromRgb(0, 231, 255),
       Color.FromRgb(0, 235, 255),
       Color.FromRgb(0, 239, 255),
       Color.FromRgb(0, 243, 255),
       Color.FromRgb(0, 247, 255),
       Color.FromRgb(0, 251, 255),
       Color.FromRgb(0, 255, 255),
       Color.FromRgb(4, 255, 251),
       Color.FromRgb(8, 255, 247),
       Color.FromRgb(12, 255, 243),
       Color.FromRgb(16, 255, 239),
       Color.FromRgb(20, 255, 235),
       Color.FromRgb(24, 255, 231),
       Color.FromRgb(28, 255, 227),
       Color.FromRgb(32, 255, 223),
       Color.FromRgb(36, 255, 219),
       Color.FromRgb(40, 255, 215),
       Color.FromRgb(44, 255, 211),
       Color.FromRgb(48, 255, 207),
       Color.FromRgb(52, 255, 203),
       Color.FromRgb(56, 255, 199),
       Color.FromRgb(60, 255, 195),
       Color.FromRgb(64, 255, 191),
       Color.FromRgb(68, 255, 187),
       Color.FromRgb(72, 255, 183),
       Color.FromRgb(76, 255, 179),
       Color.FromRgb(80, 255, 175),
       Color.FromRgb(84, 255, 171),
       Color.FromRgb(88, 255, 167),
       Color.FromRgb(92, 255, 163),
       Color.FromRgb(96, 255, 159),
       Color.FromRgb(100, 255, 155),
       Color.FromRgb(104, 255, 151),
       Color.FromRgb(108, 255, 147),
       Color.FromRgb(112, 255, 143),
       Color.FromRgb(116, 255, 139),
       Color.FromRgb(120, 255, 135),
       Color.FromRgb(124, 255, 131),
       Color.FromRgb(128, 255, 128),
       Color.FromRgb(131, 255, 124),
       Color.FromRgb(135, 255, 120),
       Color.FromRgb(139, 255, 116),
       Color.FromRgb(143, 255, 112),
       Color.FromRgb(147, 255, 108),
       Color.FromRgb(151, 255, 104),
       Color.FromRgb(155, 255, 100),
       Color.FromRgb(159, 255, 96),
       Color.FromRgb(163, 255, 92),
       Color.FromRgb(167, 255, 88),
       Color.FromRgb(171, 255, 84),
       Color.FromRgb(175, 255, 80),
       Color.FromRgb(179, 255, 76),
       Color.FromRgb(183, 255, 72),
       Color.FromRgb(187, 255, 68),
       Color.FromRgb(191, 255, 64),
       Color.FromRgb(195, 255, 60),
       Color.FromRgb(199, 255, 56),
       Color.FromRgb(203, 255, 52),
       Color.FromRgb(207, 255, 48),
       Color.FromRgb(211, 255, 44),
       Color.FromRgb(215, 255, 40),
       Color.FromRgb(219, 255, 36),
       Color.FromRgb(223, 255, 32),
       Color.FromRgb(227, 255, 28),
       Color.FromRgb(231, 255, 24),
       Color.FromRgb(235, 255, 20),
       Color.FromRgb(239, 255, 16),
       Color.FromRgb(243, 255, 12),
       Color.FromRgb(247, 255, 8),
       Color.FromRgb(251, 255, 4),
       Color.FromRgb(255, 255, 0),
       Color.FromRgb(255, 251, 0),
       Color.FromRgb(255, 247, 0),
       Color.FromRgb(255, 243, 0),
       Color.FromRgb(255, 239, 0),
       Color.FromRgb(255, 235, 0),
       Color.FromRgb(255, 231, 0),
       Color.FromRgb(255, 227, 0),
       Color.FromRgb(255, 223, 0),
       Color.FromRgb(255, 219, 0),
       Color.FromRgb(255, 215, 0),
       Color.FromRgb(255, 211, 0),
       Color.FromRgb(255, 207, 0),
       Color.FromRgb(255, 203, 0),
       Color.FromRgb(255, 199, 0),
       Color.FromRgb(255, 195, 0),
       Color.FromRgb(255, 191, 0),
       Color.FromRgb(255, 187, 0),
       Color.FromRgb(255, 183, 0),
       Color.FromRgb(255, 179, 0),
       Color.FromRgb(255, 175, 0),
       Color.FromRgb(255, 171, 0),
       Color.FromRgb(255, 167, 0),
       Color.FromRgb(255, 163, 0),
       Color.FromRgb(255, 159, 0),
       Color.FromRgb(255, 155, 0),
       Color.FromRgb(255, 151, 0),
       Color.FromRgb(255, 147, 0),
       Color.FromRgb(255, 143, 0),
       Color.FromRgb(255, 139, 0),
       Color.FromRgb(255, 135, 0),
       Color.FromRgb(255, 131, 0),
       Color.FromRgb(255, 128, 0),
       Color.FromRgb(255, 124, 0),
       Color.FromRgb(255, 120, 0),
       Color.FromRgb(255, 116, 0),
       Color.FromRgb(255, 112, 0),
       Color.FromRgb(255, 108, 0),
       Color.FromRgb(255, 104, 0),
       Color.FromRgb(255, 100, 0),
       Color.FromRgb(255, 96, 0),
       Color.FromRgb(255, 92, 0),
       Color.FromRgb(255, 88, 0),
       Color.FromRgb(255, 84, 0),
       Color.FromRgb(255, 80, 0),
       Color.FromRgb(255, 76, 0),
       Color.FromRgb(255, 72, 0),
       Color.FromRgb(255, 68, 0),
       Color.FromRgb(255, 64, 0),
       Color.FromRgb(255, 60, 0),
       Color.FromRgb(255, 56, 0),
       Color.FromRgb(255, 52, 0),
       Color.FromRgb(255, 48, 0),
       Color.FromRgb(255, 44, 0),
       Color.FromRgb(255, 40, 0),
       Color.FromRgb(255, 36, 0),
       Color.FromRgb(255, 32, 0),
       Color.FromRgb(255, 28, 0),
       Color.FromRgb(255, 24, 0),
       Color.FromRgb(255, 20, 0),
       Color.FromRgb(255, 16, 0),
       Color.FromRgb(255, 12, 0),
       Color.FromRgb(255, 8, 0),
       Color.FromRgb(255, 4, 0),
       Color.FromRgb(255, 0, 0),
       Color.FromRgb(251, 0, 0),
       Color.FromRgb(247, 0, 0),
       Color.FromRgb(243, 0, 0),
       Color.FromRgb(239, 0, 0),
       Color.FromRgb(235, 0, 0),
       Color.FromRgb(231, 0, 0),
       Color.FromRgb(227, 0, 0),
       Color.FromRgb(223, 0, 0),
       Color.FromRgb(219, 0, 0),
       Color.FromRgb(215, 0, 0),
       Color.FromRgb(211, 0, 0),
       Color.FromRgb(207, 0, 0),
       Color.FromRgb(203, 0, 0),
       Color.FromRgb(199, 0, 0),
       Color.FromRgb(195, 0, 0),
       Color.FromRgb(191, 0, 0),
       Color.FromRgb(187, 0, 0),
       Color.FromRgb(183, 0, 0),
       Color.FromRgb(179, 0, 0),
       Color.FromRgb(175, 0, 0),
       Color.FromRgb(171, 0, 0),
       Color.FromRgb(167, 0, 0),
       Color.FromRgb(163, 0, 0),
       Color.FromRgb(159, 0, 0),
       Color.FromRgb(155, 0, 0),
       Color.FromRgb(151, 0, 0),
       Color.FromRgb(147, 0, 0),
       Color.FromRgb(143, 0, 0),
       Color.FromRgb(139, 0, 0),
       Color.FromRgb(135, 0, 0),
       Color.FromRgb(131, 0, 0),
       Color.FromRgb(128, 0, 0)  };
        

        float InterpolateColor(float mine, float next)
        {
            var diff = next - mine;
            return Math.Abs(diff) < 0.0001 ? mine : mine + diff / 2;
        }
        #endregion

        private void SliderHorizontalRotation_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi)
                return;

            Chart3DSurface.PeUserInterface.Scrollbar.DegreeOfRotation = (int)SliderHorizontalRotation.Value;
            Chart3DSurface.Invalidate();
        }

        private void SliderZoom_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi)
                return;

            // Zoom towards/away the center of scene, or translated center of scene

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Chart3DSurface.PePlot.Option.DxZoom = (float)SliderZoom.Value; //  Magnifying, expanding z axis as coded, ProEssentials really considers this the Y Axis   
                Chart3DSurface.Invalidate();
            });
        }

        private void SliderVerticalLightRotation_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi)
                return;

            // Showing how ProEssentials sets light position based on 
            // horizontal degree position, moving light horizontally, rotating around the center of the scene 
            // vertical degree position, moving light vertically, rotating around the center of the scene

            if (CurrentHeightMap != null)
            {
                _HorzLightDegree = (float)SliderHorizontalLightRotation.Value;
                _VertLightDegree = (float)SliderVerticalLightRotation.Value;
                float x = (float)Math.Sin(_HorzLightDegree * 0.0174533f) * 20.0F;
                float z = (float)Math.Cos(_HorzLightDegree * 0.0174533f) * 20.0F;
                float y = (float)Math.Sin(_VertLightDegree * 0.0174533f) * 20.0F;
                Chart3DSurface.PeFunction.SetLight(0, x, y, z);
            }
            Chart3DSurface.Invalidate();
        }


        private void SliderHorizontalLightRotation_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi)
                return;

            // Showing how ProEssentials sets light position based on 
            // horizontal degree position, moving light horizontally, rotating around the center of the scene 
            // vertical degree position, moving light vertically, rotating around the center of the scene

            if (CurrentHeightMap != null)
            {
                _HorzLightDegree = (float)SliderHorizontalLightRotation.Value;
                _VertLightDegree = (float)SliderVerticalLightRotation.Value;
                float x = (float)Math.Sin(_HorzLightDegree * 0.0174533f) * 20.0F;
                float z = (float)Math.Cos(_HorzLightDegree * 0.0174533f) * 20.0F;
                float y = (float)Math.Sin(_VertLightDegree * 0.0174533f) * 20.0F;
                Chart3DSurface.PeFunction.SetLight(0, x, y, z);
            }
            Chart3DSurface.Invalidate();
        }

        private void SliderVerticalRotation_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi)
                return;

            Chart3DSurface.PeUserInterface.Scrollbar.ViewingHeight = (int)SliderVerticalRotation.Value;
            Chart3DSurface.Invalidate();
        }

        private void Chart_OnPeHorzScroll(object sender, ScrollEventArgs e)
        {
            // 3D chart built in click drag interface can rotate, so we update the UI slider to match 
            _updatingUi = true;
            SliderHorizontalRotation.Value = Chart3DSurface.PeUserInterface.Scrollbar.SBPos;
            _updatingUi = false;
        }

        private void Chart_OnPeVertScroll(object sender, ScrollEventArgs e)
        {
            // 3D chart built in click drag interface can rotate, so we update the UI slider to match 
            _updatingUi = true;
            var pos = e.Position > 32767 ? e.Position - 65536 : e.Position;
            SliderVerticalRotation.Value = Chart3DSurface.PeUserInterface.Scrollbar.ViewingHeight;
            _updatingUi = false;
        }

        private void Chart_OnPeZoomIn(object sender, EventArgs e)
        {
            // MouseWheel used to zoom 3d chart, so we update the UI slider to match the 3d chart's settings
            // this event happens when mouse wheel moves both direstions,  zooming in and or out  
            _updatingUi = true;
            SliderZoom.Value = Chart3DSurface.PePlot.Option.DxZoom;
            _updatingUi = false;
        }

        private bool _updatingUi;

        private void SliderZExaggeration_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi == true) { return; }

            if (CurrentHeightMap is null)
                return;

            if (HotSpots.IsChecked == true)
            {
                // This invokes code to disable hot spots and cursor prompt tracking. 
                // Dragging this slider invokes multiple image re-constructions as the z axis is adjusted.
                // The hot spot internal oct tree construction process is time intensive and hot spots and
                // cursor prompt tracking must be disabled if attempting many image re-constructions.  
                HotSpots.IsChecked = false;

            }

            var zExaggeration = (float)e.NewValue / 100.0f;

            var width = (float)CurrentHeightMap.WidthMm;
            var height = (float)CurrentHeightMap.HeightMm;
            var diag = (float)Math.Sqrt(width * width + height * height);

            Chart3DSurface.PeGrid.Option.GridAspectX = width;
            Chart3DSurface.PeGrid.Option.GridAspectZ = height;
            Chart3DSurface.PeGrid.Option.GridAspectY = diag * zExaggeration;

            if (_bShowingPlane)
            {
                float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
                double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * (SliderXPlane.Value / 100.0F));
                MoveXPlane(dPos);
            }

            Chart3DSurface.PeFunction.Force3dxNewColors = true;
            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeFunction.ReinitializeResetImage();
            Chart3DSurface.Invalidate();

        }

        private void HeightMaps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _bZoomed = false;
            Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.None;
            Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.None;

            Chart3DSurface.PeUserInterface.Menu.CustomMenu[UndoZoomMenu3d, 0] = CustomMenu.Grayed;
            Chart2DContour.PeGrid.Zoom.Mode = false;  // reset 2D contour zoom mode when switching data files 

            string newfile = HeightMaps.SelectedValue.ToString();
            HeightMapA = new HeightMap(newfile);
            RefreshUi(HeightMapA);

            BottomContour.IsChecked = false;         // reset bottom contour
            ShowLegend.IsChecked = true;             // reset show legend 
            ShowPlane.IsChecked = true;              // remove plane  

            if (_bShowingPlane)
            {
                float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
                double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * (SliderXPlane.Value / 100.0F));
                MoveXPlane(dPos);
            }
        }

        private void Main_Loaded(object sender, RoutedEventArgs e)
        {
            _nDataStep = _nAppliedStep;  // you can change GlobalStep, for example 3, for better performance than setting of 2.

            _updatingUi = true;

            Chart2DContainer.Visibility = Visibility.Collapsed;

            HeightMaps.Items.Add("MaterialSurfaceScan1-2464x2056.bhm");
            HeightMaps.Items.Add("MaterialSurfaceScan2-5024x2736.bhm");
            HeightMaps.Items.Add("MaterialSurfaceScan3-5024x2736.bhm");
            HeightMaps.Items.Add("GrandCanyon-4033x4033.bhm");
            HeightMaps.Items.Add("NoisyTerrain-4352x4352.bhm");
            HeightMaps.Items.Add("GrandCanyon-grayscale-rawpng-512x512.png");
            HeightMaps.Items.Add("Terrain-rgb-rawpng-1000x1000.png");
            HeightMaps.Items.Add("Cat-grayscale-rawpng-2047x1531.png");
            HeightMaps.Items.Add("leaf-grayscale-rawpng-1096x2048.png");

            ReduceDataAmount.Items.Add("None");
            ReduceDataAmount.Items.Add("2X");
            ReduceDataAmount.Items.Add("3X");
            ReduceDataAmount.Items.Add("4X");

            CenterWindowOnScreen();

            ReduceDataAmount.SelectedIndex = 1;

            _updatingUi = false;
        }

        private void CenterWindowOnScreen()
        {
            double screenWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
            double screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight;
            this.Width = screenWidth * .8F;
            this.Height = screenHeight * .8F;
            double windowWidth = this.Width;
            double windowHeight = this.Height;
            this.Left = (screenWidth / 2) - (windowWidth / 2);
            this.Top = (screenHeight / 2) - (windowHeight / 2);
        }

        private void ShowPlane_Checked(object sender, RoutedEventArgs e)
        {
            if (CurrentHeightMap is null)
                return;

            _bShowingPlane = true;

            if (SliderXPlane.Value == 40)
            {
                float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
                double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * 40.0F / 100.0F);
                MoveXPlane(dPos);

                Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
                Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
                Chart3DSurface.PeFunction.Reinitialize();
                Chart3DSurface.Invalidate();
            }
            else
            {
                SliderXPlane.Value = 40;
                Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
                Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
                Chart3DSurface.PeFunction.Reinitialize();
                Chart3DSurface.Invalidate();
            }

            Chart2DContainer.Visibility = Visibility.Visible;

        }

        private void ShowPlane_UnChecked(object sender, RoutedEventArgs e)
        {
            _bShowingPlane = false;
            Chart3DSurface.PeAnnotation.Show = false;
            Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
            Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
            Chart3DSurface.PeFunction.Reinitialize();
            Chart3DSurface.Invalidate();
            Chart2DContainer.Visibility = Visibility.Collapsed;
        }

        private void SliderXPlane_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi)
                return;

            float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
            double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * e.NewValue / 100.0F);
            MoveXPlane(dPos);
        }

        /// <summary>
        /// Called when X Plane Slider is adjusted 
        /// </summary>
        /// <param name="fPos"></param>
        private void MoveXPlane(double fPos)
        {
            if (CurrentHeightMap is null)
                return;

            Chart3DSurface.PeAnnotation.Show = true;
            Chart3DSurface.PeAnnotation.Graph.Show = true;

            // rows
            // cols 

            var x = fPos; //   6.25;
            var col = (int)(x / CurrentHeightMap.WidthMm * (CurrentHeightMap.WidthPx - (_nAppliedStep - 1) - 1));
            if (col <= 0) { col = 0; }
            if (col >= CurrentHeightMap.WidthPx) { col = CurrentHeightMap.WidthPx - 1; }

            // place the line right on the pixel
            x = col * CurrentHeightMap.Resolution;
            const byte alpha = 64;

            var index = 0;
            float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxY - Chart3DSurface.PeGrid.Configure.ManualMinY);
            float fRangeOffset = fRange * 0.40F;
            double fOffset = fRange * 0.25F;  // offset the plane intersecting line upward  

            // Create a Polygon graph annotation for translucent plane //

            Chart3DSurface.PeAnnotation.Graph.HotSpot[index] = false;
            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.StartPoly;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(alpha, 255, 255, 255);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _miny;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _minz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.AddPolyPoint;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(alpha, 255, 255, 255);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _miny;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _maxz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.AddPolyPoint;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(alpha, 255, 255, 255);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _maxy + fRangeOffset;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _maxz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.EndPolygon;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(alpha, 255, 255, 255);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _maxy + fRangeOffset;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _minz;
            index++;

            // Bound the plane with black line, with LineContinues   

            Chart3DSurface.PeAnnotation.Graph.HotSpot[index] = false;
            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.ThinSolidLine;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 0, 0, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _miny;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _minz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.LineContinue;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 0, 0, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _miny;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _maxz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.LineContinue;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 0, 0, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _maxy + fRangeOffset;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _maxz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.LineContinue;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 0, 0, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _maxy + fRangeOffset;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _minz;
            index++;

            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.LineContinue;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 0, 0, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = _miny;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = _minz;
            index++;

            // Create a Yellow line that outlines where the plane intersects the surface  //

            var row = 0;
            var yOffset = (CurrentHeightMap.MaxZMm - CurrentHeightMap.MinZMm) * 0.0005;
            var z = CurrentHeightMap.GetRowMm(row);
            var y = CurrentHeightMap.GetPel(CurrentHeightMap.HeightPx - (_nAppliedStep - 1) - row - 1, col);

            Chart3DSurface.PeAnnotation.Graph.HotSpot[index] = false;
            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.ThinSolidLine;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 255, 255, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = (y + yOffset) + fOffset;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = z;
            index++;

            for (row = 0; row < CurrentHeightMap.HeightPx - (_nAppliedStep - 1); row++)
            {
                z = CurrentHeightMap.GetRowMm(row);
                y = CurrentHeightMap.GetPel(CurrentHeightMap.HeightPx - (_nAppliedStep - 1) - row - 1, col);
                Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.LineContinue;
                Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 255, 255, 0);
                Chart3DSurface.PeAnnotation.Graph.X[index] = x;
                Chart3DSurface.PeAnnotation.Graph.Y[index] = (y + yOffset) + fOffset;
                Chart3DSurface.PeAnnotation.Graph.Z[index] = z;
                index++;
            }

            row = CurrentHeightMap.HeightPx - (_nAppliedStep - 1) - 1;
            z = CurrentHeightMap.GetRowMm(row);
            y = CurrentHeightMap.GetPel(row, col);
            Chart3DSurface.PeAnnotation.Graph.Type[index] = (int)GraphAnnotationType.EndPolyLineMedium;
            Chart3DSurface.PeAnnotation.Graph.Color[index] = Color.FromArgb(255, 255, 255, 0);
            Chart3DSurface.PeAnnotation.Graph.X[index] = x;
            Chart3DSurface.PeAnnotation.Graph.Y[index] = (y + yOffset) + fOffset;
            Chart3DSurface.PeAnnotation.Graph.Z[index] = z;

            Chart3DSurface.PeAnnotation.InFront = true;
            Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
            Chart3DSurface.Invalidate();

            // Update Data within the Right Side Chart2D cross secion chart 

            Chart2DLine.PeData.Subsets = 1;
            Chart2DLine.PeData.Points = CurrentHeightMap.HeightPx - (_nAppliedStep - 1);

            index = 0;
            row = 0;
            yOffset = (CurrentHeightMap.MaxZMm - CurrentHeightMap.MinZMm) * 0.0005;
            z = CurrentHeightMap.GetRowMm(row);
            y = CurrentHeightMap.GetPel(CurrentHeightMap.HeightPx - (_nAppliedStep - 1) - row - 1, col);

            // simple spoon feeding a small slice of data 
            Chart2DLine.PeData.Y[0, index] = (float)z;
            Chart2DLine.PeData.X[0, index] = (float)(y + yOffset);
            index++;

            float fMin = 1e35F;
            float fMax = -1e35F;
            int nMaxIndex = 0;
            int nMinIndex = 0;

            float fRangeY = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxY - Chart3DSurface.PeGrid.Configure.ManualMinY);
            for (row = 0; row < CurrentHeightMap.HeightPx - (_nAppliedStep - 1); row++)
            {
                z = CurrentHeightMap.GetRowMm(row);
                y = CurrentHeightMap.GetPel(CurrentHeightMap.HeightPx - (_nAppliedStep - 1) - row - 1, col);

                if (y > fMax) { fMax = y; nMaxIndex = row; }
                if (y < fMin) { fMin = y; nMinIndex = row; }

                // simple spoon feeding a small slice of data 
                Chart2DLine.PeData.Y[0, index] = (float)z;
                Chart2DLine.PeData.X[0, index] = (float)(y + yOffset);

                float fY = (float)(y + yOffset);
                float fYMinY = (float)(y + yOffset) - (float)Chart3DSurface.PeGrid.Configure.ManualMinY;
                float fIndex = (float)((y + yOffset) - Chart3DSurface.PeGrid.Configure.ManualMinY) / (float)fRangeY;

                fIndex = fIndex * 255.0F;
                if (fIndex > 255) { fIndex = 255; }
                Chart2DLine.PePlot.PointColors[0, index] = MyColors[(int)fIndex];
                index++;
            }

            row = CurrentHeightMap.HeightPx - (_nAppliedStep - 1) - 1;
            z = CurrentHeightMap.GetRowMm(row);
            y = CurrentHeightMap.GetPel(row, col);

            Chart2DLine.PeData.Y[0, index] = (float)z;
            Chart2DLine.PeData.X[0, index] = (float)(y + yOffset);

            Chart2DLine.PeGrid.Configure.ManualMinX = Chart3DSurface.PeGrid.Configure.ManualMinY;
            Chart2DLine.PeGrid.Configure.ManualMaxX = Chart3DSurface.PeGrid.Configure.ManualMaxY;
            Chart2DLine.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;

            int aCnt = 0;
            Chart2DLine.PeAnnotation.Graph.X[aCnt] = Chart2DLine.PeData.X[0, nMaxIndex];
            Chart2DLine.PeAnnotation.Graph.Y[aCnt] = Chart2DLine.PeData.Y[0, nMaxIndex];
            Chart2DLine.PeAnnotation.Graph.Text[aCnt] = fMax.ToString();
            Chart2DLine.PeAnnotation.Graph.Type[aCnt] = (int)GraphAnnotationType.Pointer;
            Chart2DLine.PeAnnotation.Graph.Color[aCnt] = Color.FromArgb(255, 255, 255, 255);
            aCnt++;

            Chart2DLine.PeAnnotation.Graph.X[aCnt] = Chart2DLine.PeData.X[0, nMinIndex];
            Chart2DLine.PeAnnotation.Graph.Y[aCnt] = Chart2DLine.PeData.Y[0, nMinIndex];
            Chart2DLine.PeAnnotation.Graph.Text[aCnt] = fMin.ToString();
            Chart2DLine.PeAnnotation.Graph.Type[aCnt] = (int)GraphAnnotationType.Pointer;
            Chart2DLine.PeAnnotation.Graph.Color[aCnt] = Color.FromArgb(255, 255, 255, 255);
            aCnt++;

            if (_bZoomed)
            {
                Chart2DLine.PeAnnotation.Graph.X[aCnt] = Chart2DLine.PeGrid.Configure.ManualMinX;
                Chart2DLine.PeAnnotation.Graph.Y[aCnt] = Chart3DSurface.PeGrid.Configure.ManualMinZ;
                Chart2DLine.PeAnnotation.Graph.Text[aCnt] = "";
                Chart2DLine.PeAnnotation.Graph.Type[aCnt] = (int)GraphAnnotationType.StartPoly;
                aCnt++;

                Chart2DLine.PeAnnotation.Graph.X[aCnt] = Chart2DLine.PeGrid.Configure.ManualMaxX;
                Chart2DLine.PeAnnotation.Graph.Y[aCnt] = Chart3DSurface.PeGrid.Configure.ManualMinZ;
                Chart2DLine.PeAnnotation.Graph.Text[aCnt] = "";
                Chart2DLine.PeAnnotation.Graph.Type[aCnt] = (int)GraphAnnotationType.AddPolyPoint;
                aCnt++;

                Chart2DLine.PeAnnotation.Graph.X[aCnt] = Chart2DLine.PeGrid.Configure.ManualMaxX;
                Chart2DLine.PeAnnotation.Graph.Y[aCnt] = Chart3DSurface.PeGrid.Configure.ManualMaxZ;
                Chart2DLine.PeAnnotation.Graph.Text[aCnt] = "";
                Chart2DLine.PeAnnotation.Graph.Type[aCnt] = (int)GraphAnnotationType.AddPolyPoint;
                aCnt++;

                Chart2DLine.PeAnnotation.Graph.X[aCnt] = Chart2DLine.PeGrid.Configure.ManualMinX;
                Chart2DLine.PeAnnotation.Graph.Y[aCnt] = Chart3DSurface.PeGrid.Configure.ManualMaxZ;
                Chart2DLine.PeAnnotation.Graph.Text[aCnt] = "";
                Chart2DLine.PeAnnotation.Graph.Type[aCnt] = (int)GraphAnnotationType.EndPolygon;  // index 5
                Chart2DLine.PeAnnotation.Graph.Color[aCnt] = Color.FromArgb(40, 255, 255, 255);
                aCnt++;

                Chart2DLine.PeAnnotation.Line.YAxis[0] = Chart3DSurface.PeGrid.Configure.ManualMinZ;
                Chart2DLine.PeAnnotation.Line.YAxisInFront[0] = AnnotationInFront.InFront;
                Chart2DLine.PeAnnotation.Line.YAxis[1] = Chart3DSurface.PeGrid.Configure.ManualMaxZ;
                Chart2DLine.PeAnnotation.Line.YAxisInFront[1] = AnnotationInFront.InFront;
            }
            else
            {
                Chart2DLine.PeAnnotation.Line.YAxisInFront[0] = AnnotationInFront.Hide;
                Chart2DLine.PeAnnotation.Line.YAxisInFront[1] = AnnotationInFront.Hide;
                Chart2DLine.PeAnnotation.Graph.Type[5] = (int)GraphAnnotationType.NoSymbol;
            }

            Chart2DLine.PeAnnotation.Line.YAxisShow = true;
            Chart2DLine.PeAnnotation.Graph.TextSize = 140;
            Chart2DLine.PeAnnotation.Graph.Show = true;
            Chart2DLine.PeAnnotation.Show = true;

            Chart2DContainer.Visibility = Visibility.Visible;

            Chart2DLine.PeFunction.ReinitializeResetImage();
            Chart2DLine.Invalidate();

        }

        private void ShowLegend_Checked(object sender, RoutedEventArgs e)
        {
            Chart3DSurface.PeLegend.Location = LegendLocation.Bottom;
            Chart3DSurface.PeLegend.Show = true;
            Chart3DSurface.Invalidate();
        }

        private void ShowLegend_UnChecked(object sender, RoutedEventArgs e)
        {
            Chart3DSurface.PeLegend.Show = false;
            Chart3DSurface.Invalidate();
        }

        private void ShowContour_Checked(object sender, RoutedEventArgs e)
        {
            Chart3DSurface.PePlot.Option.ShowContour = ShowContour.BottomColors;
            Chart3DSurface.Invalidate();
        }

        private void ShowContour_UnChecked(object sender, RoutedEventArgs e)
        {
            Chart3DSurface.PePlot.Option.ShowContour = ShowContour.None;
            Chart3DSurface.Invalidate();
        }

        private void HotSpots_Checked(object sender, RoutedEventArgs e)
        {
            // update the menu for the 3D chart to reflect the HotSpots check box UI control 
            Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0] = CustomMenuState.Checked;

            // any of these 3 will actually enable hotspot OctTree construction, so set all three 
            Chart3DSurface.PeUserInterface.HotSpot.Data = true;
            Chart3DSurface.PeUserInterface.Cursor.PromptTracking = true;
            Chart3DSurface.PeUserInterface.Cursor.HighlightColor = Color.FromArgb(255, 255, 0, 0);

            Chart3DSurface.PeUserInterface.Cursor.PromptStyle = CursorPromptStyle.YValue; // only y and xyz are options.
            Chart3DSurface.PeUserInterface.Cursor.PromptLocation = CursorPromptLocation.ToolTip;
            Chart3DSurface.PeData.Precision = DataPrecision.TwoDecimals;

            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeFunction.ReinitializeResetImage();
            Chart3DSurface.Invalidate();
        }

        private void HotSpots_UnChecked(object sender, RoutedEventArgs e)
        {
            // update the menu for the 3D chart to reflect the HotSpots check box UI control 
            Chart3DSurface.PeUserInterface.Menu.CustomMenuState[CursorTrackingMenu3d, 0] = CustomMenuState.UnChecked;

            // any of these 3 will actually enable hotspot OctTree construction, so reset all three 
            Chart3DSurface.PeUserInterface.HotSpot.Data = false;
            Chart3DSurface.PeUserInterface.Cursor.PromptTracking = false;
            Chart3DSurface.PeUserInterface.Cursor.HighlightColor = Color.FromArgb(0, 0, 0, 0);

            Chart3DSurface.PeUserInterface.Cursor.PromptStyle = CursorPromptStyle.None; // only y and xyz are options.

            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeFunction.ReinitializeResetImage();
            Chart3DSurface.Invalidate();
        }

        private void ReduceData_UnChecked(object sender, RoutedEventArgs e)
        {
            // Its hard to recognize the difference in images when data is reduced.
            // With the chart magnified and cursor tracking enabled, look for the red quad under the mouse.
            // This quad is two triangles bounding the highlighted data point.    
            // When showing full data, and cursor tracking, the hit testing is time intensive process and you will likely see
            // a diiference in performace of hit testing when showing full data above 2000x2000. 

            _nDataStep = 1; // show full data 

            if (_updatingUi) { return; }

            // changing data for both 3D and 2D charts, best to reset everything when changing this setting 

            RefreshUi(CurrentHeightMap);

            BottomContour.IsChecked = false;         // reset bottom contour
            ShowLegend.IsChecked = true;             // reset legend
            ShowPlane.IsChecked = true;              // remove plane  

            Chart2DContour.PeGrid.Zoom.Mode = false;
            _bZoomed = false;
            Chart3DSurface.PeUserInterface.Menu.CustomMenu[UndoZoomMenu3d, 0] = CustomMenu.Grayed;

            if (_bShowingPlane)
            {
                float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
                double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * (SliderXPlane.Value / 100.0F));
                MoveXPlane(dPos);
            }
        }

        private void ReduceDataAmount_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurrentHeightMap == null) { return; }
            if (_updatingUi) { return; }

            _nDataStep = ReduceDataAmount.SelectedIndex + 1;   // indices 0123 equals "0", "2", "3", "4"
                                                               // potential step, we will not reduce an original heightmap smaller than 1000x1000, though a large height map with 4x may go below 1000x1000

            // changing data for both 3D and 2D charts
            RefreshUi(CurrentHeightMap);

            if (_bShowingPlane)
            {
                float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
                double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * (SliderXPlane.Value / 100.0F));
                MoveXPlane(dPos);
            }
        }

        private void Chart2DContour_OnPeZoomIn(object sender, EventArgs e)
        {
            // set the zoom state of the Pe3doWpf 3D surface chart //
            // 3d really does not have a zoommode, we simply set axes to manual range mode (ManualScaleControl) and set the min and max manually  (ManualMinX, etc)

            _bZoomed = true;
            Chart3DSurface.PeUserInterface.Menu.CustomMenu[UndoZoomMenu3d, 0] = CustomMenu.Show;

            Chart3DSurface.PeGrid.Configure.DxPsManualCullXZ = true;

            Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;
            Chart3DSurface.PeGrid.Configure.ManualMinX = Chart2DContour.PeGrid.Zoom.MinX;
            Chart3DSurface.PeGrid.Configure.ManualMaxX = Chart2DContour.PeGrid.Zoom.MaxX;

            Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.MinMax;
            Chart3DSurface.PeGrid.Configure.ManualMinZ = Chart2DContour.PeGrid.Zoom.MinY;
            Chart3DSurface.PeGrid.Configure.ManualMaxZ = Chart2DContour.PeGrid.Zoom.MaxY;

            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
            Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
            Chart3DSurface.PeFunction.Reinitialize();  // needed as we are changing ManualScaleControl 
            Chart3DSurface.Invalidate();

            float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
            double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * SliderXPlane.Value / 100.0F);
            MoveXPlane(dPos);
        }

        private void Chart2DContour_OnPeZoomOut(object sender, EventArgs e)
        {
            // undo the zoom state of the Pe3doWpf 3D surface chart //

            _bZoomed = false;
            Chart3DSurface.PeUserInterface.Menu.CustomMenu[UndoZoomMenu3d, 0] = CustomMenu.Grayed;

            // 3d really does not have a zoommode, we simply set axes manual range mode (ManualScaleControl) to none/autoscale (ManualScaleControl) 
            Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.None; // means we auto scale vs manual scale, AutoMinMaxPadding then adds any padding to auto range   
            Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.None;

            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
            Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
            Chart3DSurface.PeFunction.Reinitialize();  // needed as we are changing ManualScaleControl 
            Chart3DSurface.Invalidate();

            float fRange = (float)(Chart3DSurface.PeGrid.Configure.ManualMaxX - Chart3DSurface.PeGrid.Configure.ManualMinX);
            double dPos = Chart3DSurface.PeGrid.Configure.ManualMinX + (fRange * SliderXPlane.Value / 100.0F);
            MoveXPlane(dPos);
        }

        private void Chart2DContour_PeVertScroll(object sender, ScrollEventArgs e)
        {
            // update the 3D chart with the 2D Contour zoom parameters //
            Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;
            Chart3DSurface.PeGrid.Configure.ManualMinX = Chart2DContour.PeGrid.Zoom.MinX;
            Chart3DSurface.PeGrid.Configure.ManualMaxX = Chart2DContour.PeGrid.Zoom.MaxX;

            Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.MinMax;
            Chart3DSurface.PeGrid.Configure.ManualMinZ = Chart2DContour.PeGrid.Zoom.MinY;
            Chart3DSurface.PeGrid.Configure.ManualMaxZ = Chart2DContour.PeGrid.Zoom.MaxY;

            Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
            Chart3DSurface.PeFunction.Reinitialize();  // needed as we are changing ManualScaleControl 
            Chart3DSurface.Invalidate();
        }

        private void Chart2DContour_PeHorzScroll(object sender, ScrollEventArgs e)
        {
            // update the 3D chart with the 2D Contour zoom parameters //
            Chart3DSurface.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;
            Chart3DSurface.PeGrid.Configure.ManualMinX = Chart2DContour.PeGrid.Zoom.MinX;
            Chart3DSurface.PeGrid.Configure.ManualMaxX = Chart2DContour.PeGrid.Zoom.MaxX;

            Chart3DSurface.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.MinMax;
            Chart3DSurface.PeGrid.Configure.ManualMinZ = Chart2DContour.PeGrid.Zoom.MinY;
            Chart3DSurface.PeGrid.Configure.ManualMaxZ = Chart2DContour.PeGrid.Zoom.MaxY;

            Chart3DSurface.PeFunction.Force3dxAnnotVerticeRebuild = true;
            Chart3DSurface.PeFunction.Force3dxVerticeRebuild = true;
            Chart3DSurface.PeData.SkipRanging = true;  // New feature to avoid searching for min max for this case 
            Chart3DSurface.PeFunction.Reinitialize();  // needed as we are changing ManualScaleControl 
            Chart3DSurface.Invalidate();
        }

        private void Main_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Chart2DContour.Chart != null)
            {
                // This logic is needed in complex scenarios where the overall layout changes with respect to parent but does not trigger a size changed event. 
                // For example the Main Window changes state from Maximized to Normal and a chart control position changes within the MainWindow
                // and not tied directly to a grid size. This call simply updates our DLL to understand where the wpf chart exists within the MainWindow. 
                Chart2DContour.Chart.UpdateRelativeLocation();
            }
        }

        private void SliderHorizontalMove_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi) { return; }
            Chart3DSurface.PePlot.Option.DxViewportX = (float)e.NewValue;     // controls / tweaks position of surface within the chart  
            Chart3DSurface.Invalidate();
        }

        private void SliderVerticalMove_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingUi) { return; }
            Chart3DSurface.PePlot.Option.DxViewportY = (float)e.NewValue;     // controls / tweaks position of surface within the chart  
            Chart3DSurface.Invalidate();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string hs = "3D Chart \n";
            hs += "1. Left button and drag to rotate.\n";
            hs += "2. Shift + Left button drag to translate surface.\n";
            hs += "3. Mouse Wheel zooms in and out entire scene.\n";
            hs += "4. Middle button and drag adjusts light location.\n";
            hs += "5. Right click shows popup menu.\n\n";
            hs += "2D Contour - Bottom Left \n";
            hs += "1. Left button and drag to zoom. Also zooms 3D Chart3DSurface.\n";
            hs += "2. Right click shows popup menu. \n";
            hs += "3. Right click UndoZoom to undo the 3D Chart's zoom. \n\n";
            hs += "Sliders allow manipulation similar to click and drag built in UI.\n\n";
            hs += "Explode Z Axis slider adjusts the aspect ratio of z axis to xy axes. This process requires the triangle data to be re-constructed and is gpu intensive.\n\n";
            hs += "Cursor Tracking enables cursor prompt tracking of mouse across 3D surface. This setting may reset itself based on various UI behaviors.\n";
            hs += "For large surfaces, the hit testing is accomplished cpu-side and is cpu-intensive to build the huge octree and then search the octree during mousemove events. \n\n";
            hs += "Reduce Data setting provides more responsive cursor tracking. Files are so large, reducing data may show little visual difference. \n\n";
            hs += "Reduce Data setting will help old computers without a dedicated GPU or improved integrated graphics.\n\n";
            hs += "Performance is dependent on the cpu/gpu. Old computers without a gpu (or poor integrated graphics) may struggle with this amount of data. Though Gigasoft plots this data faster than any other known Chart3DSurface. ";


            MessageBox.Show(hs);
        }

    }

}