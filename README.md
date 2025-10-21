# GliderIMUViewer

GliderIMUViewer is a Unity-based application for visualizing Inertial Measurement Unit (IMU) pose data from CSV files. It provides interactive graph visualization of roll, pitch, and yaw angles with real-time playback capabilities.

## Overview

This application enables researchers, engineers, and developers to analyze and visualize orientation data captured from IMU sensors, particularly those used in glider or aircraft applications. The viewer supports multiple coordinate systems, angle units (degrees/radians), and deploys across multiple platforms including Windows, macOS, Linux standalone builds, and WebGL.

## Features

- **CSV File Loading**: Load IMU pose data from CSV files with cross-platform file dialog support
- **Interactive Graph Visualization**: Display roll, pitch, and yaw angles as time-series graphs
- **Real-time Playback**: Control playback timing with visual cursor tracking
- **Flexible Data Format**: Support for various CSV formats with configurable column names
- **Angle Unit Detection**: Automatic detection of degrees vs radians
- **Coordinate System Mapping**: Configurable axis mapping and sign conventions
- **Multi-Platform Support**: 
  - Unity Editor (EditorUtility.OpenFilePanel)
  - Windows (Ookii.Dialogs / System.Windows.Forms)
  - macOS (NSOpenPanel/NSSavePanel via Objective-C)
  - Linux (GTK-based dialogs)
  - WebGL (JavaScript file picker with Base64 encoding)

## Requirements

- Unity 2022.3 or later
- Universal Render Pipeline (URP) 17.0.4
- Unity Input System 1.14.0
- TextMeshPro

## Project Structure

```
Assets/
├── Scenes/
│   ├── KinematicPreview.unity      # Main IMU visualization scene
│   └── FlightSimulation.unity      # Flight simulation scene
├── Scripts/
│   ├── CsvOpenDialogButton.cs      # File selection UI component
│   ├── KinematicPreview/
│   │   ├── PoseCsvStore.cs         # CSV data loading and management
│   │   ├── PoseGraphView.cs        # Graph visualization component
│   │   ├── PosePlaybackController.cs  # Playback control
│   │   ├── PoseApplier.cs          # Apply pose to 3D objects
│   │   ├── OrbitAroundOrigin.cs    # Camera orbit control
│   │   └── UIShowHide.cs           # UI visibility control
│   └── FlightSimulation/
│       ├── GliderAero.cs           # Glider aerodynamics
│       ├── GliderKinematicByPlayback.cs  # Playback-based kinematics
│       └── LocalZInputMover.cs     # Input-based movement
├── Plugins/
│   └── WebGL/
│       └── FilePicker.jslib        # WebGL file picker implementation
├── StandaloneFileBrowser/          # Cross-platform file browser library
├── StreamingAssets/                # Sample CSV files
└── Settings/
    └── Mobile_RPAsset.asset        # URP rendering configuration
```

## CSV File Format

The application expects CSV files with the following structure:

### Required Columns

- **Time column**: `t_ms` (milliseconds) or `dt_ms` (delta time in milliseconds)
- **Angle columns**: `roll`, `pitch`, `yaw`

### Optional Columns

- **Acceleration**: `ax`, `ay`, `az` (in m/s² or G)
- **Custom columns**: Any additional numeric or string columns

### Example CSV Format

```csv
t_ms,roll,pitch,yaw,ax,ay,az
0,0.0,0.0,0.0,0.0,0.0,9.81
10,0.5,0.2,0.1,0.1,0.0,9.80
20,1.0,0.4,0.2,0.2,0.0,9.79
...
```

### Configuration Options

The `PoseCsvStore` component provides extensive configuration:

- **Column Names**: Customize column names for time, angles, and acceleration
- **Delimiter**: Configure CSV delimiter (default: comma)
- **Angle Units**: Auto-detect or manually specify degrees/radians
- **Axis Mapping**: Map roll/pitch/yaw to X/Y/Z axes
- **Sign Convention**: Configure sign multipliers for each axis
- **Normalization**: Option to zero angles at start
- **Acceleration Units**: Support for m/s² or G units

## Getting Started

### Opening the Project

1. Clone this repository
2. Open the project in Unity 2022.3 or later
3. Open the `KinematicPreview` scene from `Assets/Scenes/`

### Loading CSV Data

#### Method 1: Using the UI Button

1. Run the scene in Unity Editor or build the application
2. Click the "Open CSV" button
3. Select your CSV file from the file dialog
4. The graph will automatically update with the loaded data

#### Method 2: Using StreamingAssets

1. Place your CSV file in `Assets/StreamingAssets/`
2. In the `PoseCsvStore` component:
   - Enable "Use Streaming Assets Path"
   - Set "File Path" to your CSV filename
   - Enable "Load On Start"
3. Run the scene

### Configuring Data Import

Select the GameObject with the `PoseCsvStore` component and configure:

1. **CSV Settings**:
   - Set column names to match your CSV file
   - Configure delimiter if not using comma
   
2. **Angle Units & Axis Map**:
   - Enable "Auto Detect Angle Units" or manually specify
   - Map roll/pitch/yaw to appropriate axes
   - Set sign multipliers if needed

3. **Normalization**:
   - Enable "Zero At Start" to subtract initial pose

## Key Components

### PoseCsvStore

Central data store for loading, parsing, and managing pose CSV data. Provides:

- `LoadFromAbsolutePath(string path)`: Load CSV from absolute file path
- `EvaluateQuat(float time)`: Get interpolated pose quaternion at given time
- `GetSnapshot(out float[] times, out Vector3[] eulersDeg)`: Get complete time series data
- Events: `Loaded`, `LoadFailed`

### PoseGraphView

Unity UI Graphic component that renders pose data as interactive graphs:

- Displays roll, pitch, and yaw as colored line series
- Real-time playback cursor
- Auto-scaling or fixed Y-axis range
- Configurable colors and line thickness

### CsvOpenDialogButton

UI component managing cross-platform file selection:

- Handles platform-specific file dialog implementations
- Validates file selection and extension
- Provides status feedback
- Remembers last directory

### PosePlaybackController

Manages playback timing and animation state:

- Play/pause control
- Time scrubbing
- Playback speed adjustment
- Loop control

## Building the Application

### Standalone Builds (Windows/macOS/Linux)

1. Go to File > Build Settings
2. Select your target platform
3. Add the `KinematicPreview` scene
4. Click "Build" or "Build and Run"

The standalone file browser will use native OS dialogs.

### WebGL Build

1. Go to File > Build Settings
2. Select "WebGL" platform
3. Add the `KinematicPreview` scene
4. Click "Build"

The WebGL build uses JavaScript file picker with Base64 encoding for file loading.

## Platform-Specific Notes

### Windows
Uses `StandaloneFileBrowser` with Windows Forms dialogs.

### macOS
Uses native `NSOpenPanel`/`NSSavePanel` through Objective-C bundle.

### Linux
Uses GTK-based dialogs through native library.

### WebGL
- File selection uses browser's native file picker
- Files are loaded via Base64 encoding
- No direct file system access

## Sample Data

A sample CSV file is included in `Assets/StreamingAssets/out_20250912-152517.csv` for testing purposes.

## Troubleshooting

### CSV Not Loading

- Check that column names in `PoseCsvStore` match your CSV headers
- Verify delimiter setting matches your CSV format
- Ensure time column exists (either `t_ms` or `dt_ms`)
- Check Unity Console for error messages

### Angles Look Wrong

- Try toggling "Auto Detect Angle Units"
- Verify axis mapping (rollAxis, pitchAxis, yawAxis)
- Check sign multipliers (rollSign, pitchSign, yawSign)
- Enable "Zero At Start" if initial pose should be identity

### File Dialog Not Working

- **Editor**: Should always work with EditorUtility
- **Standalone**: Ensure StandaloneFileBrowser plugins are properly imported
- **WebGL**: Must be running in browser, not Unity Editor

## License

This project uses the following third-party libraries:

- **StandaloneFileBrowser**: Cross-platform file browser for Unity
- **TextMeshPro**: Unity's text rendering solution
- **Universal Render Pipeline**: Unity's scriptable render pipeline

## Version

Current version: 1.1.0

## Unity Version

Built with Unity 2022.3 LTS using Universal Render Pipeline.
