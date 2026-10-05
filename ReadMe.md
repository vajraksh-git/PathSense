# PathSense 3D

PathSense 3D is a Unity-based enterprise Digital Twin for tracking truck fleets in open-cast mining operations.

## Setup Guide

### 1. Python Environment Setup
You need Python installed for the simulation script.
1. Create a virtual environment:
   ```bash
   python -m venv venv
   ```
2. Activate the virtual environment:
   - Windows: `venv\Scripts\activate`
   - Mac/Linux: `source venv/bin/activate`
3. Install the required dependencies:
   ```bash
   pip install pyserial keyboard
   ```

### 2. Unity Setup
1. Open the **Unity Hub** and click **Add** or **Open**.
2. Select the `PathSense_3D` folder as the project directory.
3. Use Unity 6 (or the matching version requested by the project).
4. Once the project opens, go to `Assets/Scenes` and open the main scene (`SampleScene` or similar).

### 3. com0com Virtual Serial Port Setup
The simulator uses a virtual serial port bridge to communicate with Unity.
1. Download and install [com0com](https://sourceforge.net/projects/com0com/).
2. Open the com0com Setup utility.
3. Create a new virtual port pair with names `COM20` and `COM8` (or use default ones and configure them in Unity).
   - If you use different ports, make sure to update the COM port in `sim.py` and the `GroundStationTwinController` in Unity.
4. Apply the settings.

### 4. Running the Project
1. Press **Play** in Unity.
2. In a terminal, run the simulator: 
   ```bash
   python sim.py
   ```
3. Control Truck 1 with `WASD` and `F` (Sonar). Control Truck 2 with `Arrow Keys` and `1` (Sonar).
