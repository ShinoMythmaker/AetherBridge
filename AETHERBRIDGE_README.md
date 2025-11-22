# AetherBridge

A Dalamud plugin for Final Fantasy XIV that exposes poseable character data to external tools like Blender.

## Features

- **Character Tracking**: Automatically tracks all player characters in the game world
- **HTTP API**: Exposes character data via a local HTTP server
- **Active Character Selection**: Mark specific characters as active for posing
- **Real-time Updates**: Character positions and rotations update in real-time
- **Brio Integration**: Integrates with Brio plugin for advanced pose manipulation
- **Bidirectional Pose Control**: Read and write character poses from external tools
- **Blender Integration**: Ready for integration with Blender add-ons

## Installation

1. Make sure you have [XIVLauncher](https://goatcorp.github.io/) installed
2. **(Optional but Recommended)** Install [Brio](https://github.com/Etheirys/Brio) for pose manipulation features
3. Copy the compiled plugin DLL to your Dalamud plugins folder:
   - Default location: `%AppData%\XIVLauncher\devPlugins\`
4. Enable the plugin in the Dalamud plugin manager

## Requirements

### Core Features (Character Tracking & HTTP API)
- Final Fantasy XIV with XIVLauncher/Dalamud
- No additional plugins required

### Pose Manipulation Features
- **Brio plugin** (v2.0+) - For reading and writing character poses
- GPose mode for full pose control
- AetherBridge will automatically detect Brio via IPC

## Usage

### In-Game

1. Type `/bridge` in the chat to open the AetherBridge window
2. Click **Start Server** to enable the HTTP API
3. Characters in the game world will appear in the character list
4. Click the checkbox next to a character to mark them as **Active** for posing
5. Active characters will be highlighted in green and remain tracked even when they move out of range

### Configuration

Click the **Settings** button to configure:
- **Server Port**: The port the HTTP server listens on (default: 8765)
- **Auto-start server**: Automatically start the server when logging in

### API Endpoints

Once the server is running, you can access:

#### `GET http://localhost:8765/status`
Returns server status information:
```json
{
  "status": "online",
  "version": "1.0.0",
  "brioAvailable": true,
  "timestamp": "2025-11-22T12:00:00Z"
}
```

#### `GET http://localhost:8765/characters`
Returns the list of poseable characters:
```json
[
  {
    "objectId": 123456789,
    "name": "Character Name",
    "position": {
      "x": 100.5,
      "y": 10.2,
      "z": 200.3
    },
    "rotation": {
      "x": 0.0,
      "y": 1.57,
      "z": 0.0
    },
    "scale": {
      "x": 1.0,
      "y": 1.0,
      "z": 1.0
    },
    "currentPose": "Idle",
    "isActive": true,
    "modelId": 1,
    "lastUpdated": "2025-11-22T12:00:00Z"
  }
]
```

#### `GET http://localhost:8765/character/{objectId}/pose`
Get the full pose data for a specific character (requires Brio):
```json
{
  "bones": {
    "Root": {
      "position": {"x": 0, "y": 0, "z": 0},
      "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
      "scale": {"x": 1, "y": 1, "z": 1}
    }
    // ... more bones
  }
}
```

#### `POST http://localhost:8765/character/{objectId}/pose`
Set the pose for a specific character (requires Brio):
```bash
curl -X POST http://localhost:8765/character/123456789/pose \
  -H "Content-Type: application/json" \
  -d @pose_data.json
```

Response:
```json
{
  "success": true,
  "objectId": 123456789
}
```

#### `GET http://localhost:8765/character/{objectId}/transform`
Get the transform (position, rotation, scale) for a character:
```json
{
  "objectId": 123456789,
  "position": {"x": 100.5, "y": 10.2, "z": 200.3},
  "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
  "scale": {"x": 1, "y": 1, "z": 1}
}
```

#### `POST http://localhost:8765/character/{objectId}/transform`
Set the transform for a character (requires Brio):
```bash
curl -X POST http://localhost:8765/character/123456789/transform \
  -H "Content-Type: application/json" \
  -d '{
    "position": {"x": 100, "y": 10, "z": 200},
    "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
    "scale": {"x": 1, "y": 1, "z": 1},
    "additive": false
  }'
```

#### `POST http://localhost:8765/character/{objectId}/bones`
Set bone transforms for a character. Supports two methods:

**Method 1: Brio (default)** - Compatible but limited to ~1 FPS due to Brio's pose loading system:
```bash
curl -X POST "http://localhost:8765/character/123456789/bones" \
  -H "Content-Type: application/json" \
  -d '{
    "j_kosi": {
      "position": {"x": 0, "y": 0, "z": 0},
      "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
      "scale": {"x": 1, "y": 1, "z": 1}
    },
    "j_sebo_a": {
      "position": {"x": 0, "y": 0, "z": 0},
      "rotation": {"x": 0.1, "y": 0, "z": 0, "w": 0.995},
      "scale": {"x": 1, "y": 1, "z": 1}
    }
  }'
```

**Method 2: Direct (real-time)** - Bypasses Brio for 30+ FPS animation playback:
```bash
curl -X POST "http://localhost:8765/character/123456789/bones?method=direct" \
  -H "Content-Type: application/json" \
  -d '{
    "j_kosi": {
      "position": {"x": 0, "y": 0, "z": 0},
      "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
      "scale": {"x": 1, "y": 1, "z": 1}
    }
  }'
```

Response:
```json
{
  "success": true,
  "objectId": 123456789,
  "bonesUpdated": 2,
  "method": "direct"
}
```

**Direct Mode Features:**
- Bypasses Brio's pose system for real-time performance
- Directly manipulates FFXIV's Havok animation skeleton structures
- Supports 30+ FPS animation playback
- Uses `hkaPose->AccessBoneModelSpace()` for direct memory writes
- Ideal for streaming animations from Blender

**Performance Comparison:**
- **Brio mode**: ~1 FPS max (artifacts above that)
- **Direct mode**: 30+ FPS real-time animation

## Blender Integration

This plugin is designed to work with a Blender add-on (not included) that can:
1. Poll the `/characters` endpoint to get character list
2. Create/update empties or armatures in Blender based on character data
3. **Send pose data back to FFXIV** using the pose endpoints (requires Brio)
4. Live-sync character transforms between Blender and FFXIV

### Requirements for Pose Manipulation
- **Brio plugin** must be installed and running in FFXIV
- AetherBridge will detect Brio automatically via IPC
- Characters must be in GPose mode for full pose control

### Example Python Code for Blender

#### Reading Character Data:
```python
import bpy
import requests
import json

def fetch_characters():
    try:
        response = requests.get('http://localhost:8765/characters')
        if response.status_code == 200:
            return response.json()
    except Exception as e:
        print(f"Error fetching characters: {e}")
    return []

def update_scene():
    characters = fetch_characters()
    for char in characters:
        if char['isActive']:
            # Create or update empty/armature for this character
            obj_name = f"FFXIV_{char['name']}"
            if obj_name not in bpy.data.objects:
                bpy.ops.object.empty_add(type='ARROWS')
                empty = bpy.context.active_object
                empty.name = obj_name
            else:
                empty = bpy.data.objects[obj_name]
            
            # Update position
            pos = char['position']
            empty.location = (pos['x'], pos['z'], pos['y'])  # Convert FFXIV coords to Blender
            
            # Update rotation
            rot = char['rotation']
            empty.rotation_euler = (rot['x'], rot['z'], rot['y'])

# Run this periodically (e.g., as a timer)
bpy.app.timers.register(update_scene, first_interval=1.0, persistent=True)
```

#### Writing Pose Data to FFXIV:
```python
import bpy
import requests
import json
from mathutils import Quaternion

def send_pose_to_ffxiv(object_id, armature):
    """Send armature pose data to FFXIV character"""
    pose_data = {}
    
    # Build pose data from armature bones
    for bone in armature.pose.bones:
        bone_data = {
            "position": {
                "x": bone.location.x,
                "y": bone.location.z,  # Convert Blender Y/Z to FFXIV
                "z": bone.location.y
            },
            "rotation": {
                "x": bone.rotation_quaternion.x,
                "y": bone.rotation_quaternion.z,
                "z": bone.rotation_quaternion.y,
                "w": bone.rotation_quaternion.w
            },
            "scale": {
                "x": bone.scale.x,
                "y": bone.scale.z,
                "z": bone.scale.y
            }
        }
        pose_data[bone.name] = bone_data
    
    # Send to AetherBridge
    url = f"http://localhost:8765/character/{object_id}/pose"
    response = requests.post(url, json=pose_data)
    
    if response.status_code == 200:
        print(f"Pose updated for character {object_id}")
    else:
        print(f"Failed to update pose: {response.text}")

def send_transform_to_ffxiv(object_id, obj):
    """Send object transform to FFXIV character"""
    transform_data = {
        "position": {
            "x": obj.location.x,
            "y": obj.location.z,
            "z": obj.location.y
        },
        "rotation": {
            "x": obj.rotation_quaternion.x,
            "y": obj.rotation_quaternion.z,
            "z": obj.rotation_quaternion.y,
            "w": obj.rotation_quaternion.w
        },
        "scale": {
            "x": obj.scale.x,
            "y": obj.scale.z,
            "z": obj.scale.y
        },
        "additive": False
    }
    
    url = f"http://localhost:8765/character/{object_id}/transform"
    response = requests.post(url, json=transform_data)
    
    if response.status_code == 200:
        print(f"Transform updated for character {object_id}")
    else:
        print(f"Failed to update transform: {response.text}")

# Example: Update pose when user moves bones
def on_frame_change(scene):
    armature = bpy.data.objects.get("FFXIV_MyCharacter")
    if armature and armature.type == 'ARMATURE':
        object_id = 123456789  # Get this from character data
        send_pose_to_ffxiv(object_id, armature)

bpy.app.handlers.frame_change_post.append(on_frame_change)
```

#### Getting Full Pose Data from FFXIV:
```python
def fetch_pose_from_ffxiv(object_id):
    """Fetch current pose data from FFXIV character"""
    url = f"http://localhost:8765/character/{object_id}/pose"
    
    try:
        response = requests.get(url)
        if response.status_code == 200:
            return response.json()
        else:
            print(f"Failed to fetch pose: {response.status_code}")
            return None
    except Exception as e:
        print(f"Error fetching pose: {e}")
        return None

def apply_pose_to_armature(armature, pose_data):
    """Apply FFXIV pose data to Blender armature"""
    for bone_name, bone_transform in pose_data.get('bones', {}).items():
        if bone_name in armature.pose.bones:
            bone = armature.pose.bones[bone_name]
            
            # Apply position
            pos = bone_transform['position']
            bone.location = (pos['x'], pos['z'], pos['y'])
            
            # Apply rotation
            rot = bone_transform['rotation']
            bone.rotation_quaternion = Quaternion((rot['w'], rot['x'], rot['z'], rot['y']))
            
            # Apply scale
            scale = bone_transform['scale']
            bone.scale = (scale['x'], scale['z'], scale['y'])
```

## Development

### Building

```bash
dotnet build
```

### Project Structure

- `Models/PoseableCharacter.cs` - Data model for character information with pose/bone data
- `Services/CharacterService.cs` - Tracks and manages characters in the game world
- `Services/PoseService.cs` - Interfaces with Brio for pose manipulation via IPC
- `Services/BridgeServer.cs` - HTTP server that exposes the REST API
- `Windows/MainWindow.cs` - Main UI showing character list and server controls
- `Windows/ConfigWindow.cs` - Configuration settings UI

## License

This project is licensed under AGPL-3.0-or-later.

## Acknowledgments

- Built using [Dalamud](https://github.com/goatcorp/Dalamud)
- Inspired by [Brio](https://github.com/Sebane1/Brio)
