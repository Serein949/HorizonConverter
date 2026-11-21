"""Headless Blender preview of an OBJ exported by Aerofly 转换器.

Usage:
    blender -b --factory-startup -P blender_preview.py -- model.obj out.png [width height]
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src = argv[0]
out = argv[1]
width = int(argv[2]) if len(argv) > 2 else 1000
height = int(argv[3]) if len(argv) > 3 else 700

# clean scene
bpy.ops.wm.read_factory_settings(use_empty=True)

bpy.ops.wm.obj_import(filepath=src, forward_axis='Y', up_axis='Z')
objects = [o for o in bpy.context.scene.objects if o.type == 'MESH']
print(f"imported {len(objects)} mesh objects, "
      f"{sum(len(o.data.polygons) for o in objects)} faces, "
      f"{sum(len(o.material_slots) for o in objects)} material slots")

# bounding box over all meshes
lo = Vector((1e18, 1e18, 1e18))
hi = Vector((-1e18, -1e18, -1e18))
for obj in objects:
    for corner in obj.bound_box:
        world = obj.matrix_world @ Vector(corner)
        lo = Vector((min(lo[i], world[i]) for i in range(3)))
        hi = Vector((max(hi[i], world[i]) for i in range(3)))
center = (lo + hi) / 2
size = max((hi - lo)[i] for i in range(3)) or 1.0

scene = bpy.context.scene
scene.render.engine = 'BLENDER_EEVEE_NEXT'
scene.render.resolution_x = width
scene.render.resolution_y = height
scene.render.film_transparent = False
scene.world = bpy.data.worlds.new("World")
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.06, 0.08, 1)
scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.6

# sun + fill
sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN'))
sun.data.energy = 4.0
sun.rotation_euler = (math.radians(55), 0, math.radians(35))
scene.collection.objects.link(sun)

fill = bpy.data.objects.new("Fill", bpy.data.lights.new("Fill", 'SUN'))
fill.data.energy = 1.5
fill.rotation_euler = (math.radians(70), 0, math.radians(-140))
scene.collection.objects.link(fill)

cam_data = bpy.data.cameras.new("Cam")
cam_data.type = 'ORTHO'
cam_data.ortho_scale = size * 1.25
cam = bpy.data.objects.new("Cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam

direction = Vector((0.62, -0.72, 0.32)).normalized()
cam.location = center + direction * size * 2.2
look = (center - cam.location).normalized()
cam.rotation_euler = look.to_track_quat('-Z', 'Z').to_euler()

scene.render.filepath = out
scene.render.image_settings.file_format = 'PNG'
bpy.ops.render.render(write_still=True)
print("saved", out)
