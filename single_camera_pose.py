"""
单摄像头 MediaPipe Pose 驱动 (备用)

用法:
  python single_camera_pose.py

默认:
  摄像头 0 → UDP port 8888
"""

import cv2
import mediapipe as mp
import socket
import json
import time
from collections import deque

# ═══════════════════════════════════════
# 配置
# ═══════════════════════════════════════
CAM_ID = 0
UDP_PORT = 8888
UDP_IP = "127.0.0.1"
CAM_WIDTH = 640
CAM_HEIGHT = 480
FPS_TARGET = 30
MODEL_COMPLEXITY = 1
MIN_DETECTION_CONFIDENCE = 0.5
MIN_TRACKING_CONFIDENCE = 0.5
SHOW_WINDOW = True
FPS_WINDOW = 30

# ═══════════════════════════════════════
# UDP
# ═══════════════════════════════════════
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 65536)
addr = (UDP_IP, UDP_PORT)
send_times = deque(maxlen=FPS_WINDOW)
frame_count = 0

# ═══════════════════════════════════════
# MediaPipe
# ═══════════════════════════════════════
mp_pose = mp.solutions.pose
mp_draw = mp.solutions.drawing_utils
mp_style = mp.solutions.drawing_styles

pose = mp_pose.Pose(
    static_image_mode=False,
    model_complexity=MODEL_COMPLEXITY,
    min_detection_confidence=MIN_DETECTION_CONFIDENCE,
    min_tracking_confidence=MIN_TRACKING_CONFIDENCE,
)

# ═══════════════════════════════════════
# 摄像头
# ═══════════════════════════════════════
cap = cv2.VideoCapture(CAM_ID, cv2.CAP_DSHOW)
cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAM_WIDTH)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAM_HEIGHT)
cap.set(cv2.CAP_PROP_FPS, FPS_TARGET)

if not cap.isOpened():
    print(f"❌ 无法打开摄像头 {CAM_ID}")
    exit(1)

print(f"✅ 摄像头 {CAM_ID} 已打开 ({int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))}x{int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))})")
print(f"   UDP → {UDP_IP}:{UDP_PORT}")
print(f"   按 Ctrl+C 或 ESC 退出\n")

frame_times = deque(maxlen=FPS_WINDOW)

try:
    while True:
        ret, frame = cap.read()
        if not ret:
            print("⚠️ 读取失败")
            time.sleep(0.01)
            continue

        # 水平镜像 (照镜子效果)
        frame = cv2.flip(frame, 1)

        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        results = pose.process(rgb)
        rgb.flags.writeable = True
        frame = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)

        if results.pose_landmarks:
            lm_dict = {}
            for idx, lm in enumerate(results.pose_landmarks.landmark):
                # Python 端已 flip，所以 x = 1 - x
                lm_dict[str(idx)] = {
                    "x": 1.0 - lm.x,
                    "y": lm.y,
                    "z": lm.z,
                    "visibility": lm.visibility,
                }

            data = json.dumps(lm_dict).encode("utf-8")
            sock.sendto(data, addr)
            frame_count += 1
            send_times.append(time.time())

            if SHOW_WINDOW:
                mp_draw.draw_landmarks(
                    frame, results.pose_landmarks, mp_pose.POSE_CONNECTIONS,
                    landmark_drawing_spec=mp_style.get_default_pose_landmarks_style(),
                )

        frame_times.append(time.time())
        cam_fps = (len(frame_times) - 1) / (frame_times[-1] - frame_times[0]) if len(frame_times) >= 2 else 0
        udp_fps = (len(send_times) - 1) / (send_times[-1] - send_times[0]) if len(send_times) >= 2 else 0

        if SHOW_WINDOW:
            cv2.putText(frame,
                        f"FPS:{cam_fps:.0f}  UDP:{udp_fps:.0f}  Frame:{frame_count}",
                        (6, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 1)
            cv2.imshow("MediaPipe Pose", frame)
            if cv2.waitKey(1) & 0xFF == 27:
                break

except KeyboardInterrupt:
    print("\n⏹ 用户中断")

finally:
    cap.release()
    pose.close()
    sock.close()
    cv2.destroyAllWindows()
    print(f"已退出 (共发送 {frame_count} 帧)")
