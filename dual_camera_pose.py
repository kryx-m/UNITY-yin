"""
双摄像头 MediaPipe Pose 驱动 — 前摄 + 左摄正交放置

  前摄 (camera 0, 正面眼高) → UDP port 8888
  左摄 (camera 1, 侧面眼高) → UDP port 8889

用法:
  python dual_camera_pose.py

依赖:
  pip install mediapipe opencv-python numpy
"""

import cv2
import mediapipe as mp
import socket
import json
import time
import sys
import threading
from collections import deque

# ═══════════════════════════════════════════════════════════════
# 配置
# ═══════════════════════════════════════════════════════════════

# 前摄 (正面, 正对人物)
FRONT_CAM_ID = 0
FRONT_UDP_PORT = 8888
FRONT_UDP_IP = "127.0.0.1"
FRONT_WINDOW_TITLE = "Front Camera (正面)"

# 左摄 (侧面, 人物左侧)
LEFT_CAM_ID = 1
LEFT_UDP_PORT = 8889
LEFT_UDP_IP = "127.0.0.1"
LEFT_WINDOW_TITLE = "Left Camera (左侧面)"

# 通用
CAM_WIDTH = 640
CAM_HEIGHT = 480
FPS_TARGET = 30
FRAME_SKIP = 0  # 0=不跳帧, 1=隔一帧, 2=隔两帧...

# MediaPipe
MODEL_COMPLEXITY = 1          # 0=Lite, 1=Full, 2=Heavy
MIN_DETECTION_CONFIDENCE = 0.5
MIN_TRACKING_CONFIDENCE = 0.5
STATIC_IMAGE_MODE = False

# 是否显示摄像头画面
SHOW_WINDOWS = True

# UDP 帧统计窗口
FPS_WINDOW = 30

# ═══════════════════════════════════════════════════════════════
# UDP 发送器
# ═══════════════════════════════════════════════════════════════

class UdpSender:
    def __init__(self, ip, port, label=""):
        self.label = label
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.addr = (ip, port)
        self.frame_count = 0
        self.send_times = deque(maxlen=FPS_WINDOW)
        print(f"[{self.label}] UDP → {ip}:{port}")

    def send_landmarks(self, landmarks_dict):
        """发送一帧 landmarks JSON"""
        data = json.dumps(landmarks_dict).encode("utf-8")
        try:
            self.sock.sendto(data, self.addr)
            self.frame_count += 1
            self.send_times.append(time.time())
        except Exception as e:
            print(f"[{self.label}] 发送失败: {e}")

    def get_fps(self):
        if len(self.send_times) < 2:
            return 0.0
        dt = self.send_times[-1] - self.send_times[0]
        return (len(self.send_times) - 1) / dt if dt > 0 else 0.0

    def close(self):
        self.sock.close()
        print(f"[{self.label}] 已关闭 (共发送 {self.frame_count} 帧)")

# ═══════════════════════════════════════════════════════════════
# 单路摄像头处理器
# ═══════════════════════════════════════════════════════════════

class CameraProcessor:
    def __init__(self, cam_id, udp_sender, window_title, flip_horizontal=False):
        self.cam_id = cam_id
        self.udp = udp_sender
        self.window_title = window_title
        self.flip_horizontal = flip_horizontal

        # MediaPipe
        self.mp_pose = mp.solutions.pose
        self.mp_draw = mp.solutions.drawing_utils
        self.mp_style = mp.solutions.drawing_styles

        self.pose = self.mp_pose.Pose(
            static_image_mode=STATIC_IMAGE_MODE,
            model_complexity=MODEL_COMPLEXITY,
            min_detection_confidence=MIN_DETECTION_CONFIDENCE,
            min_tracking_confidence=MIN_TRACKING_CONFIDENCE,
        )

        self.cap = None
        self.running = False
        self.thread = None

        # 帧率统计
        self.frame_times = deque(maxlen=FPS_WINDOW)
        self.frame_count = 0

    def start(self):
        self.cap = cv2.VideoCapture(self.cam_id, cv2.CAP_DSHOW)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAM_WIDTH)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAM_HEIGHT)
        self.cap.set(cv2.CAP_PROP_FPS, FPS_TARGET)

        if not self.cap.isOpened():
            print(f"❌ 无法打开摄像头 {self.cam_id}")
            return False

        actual_w = self.cap.get(cv2.CAP_PROP_FRAME_WIDTH)
        actual_h = self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT)
        print(f"[{self.udp.label}] 摄像头 {self.cam_id} 已打开 ({int(actual_w)}x{int(actual_h)})")

        self.running = True
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()
        return True

    def _loop(self):
        skip_counter = 0
        while self.running:
            ret, frame = self.cap.read()
            if not ret:
                print(f"[{self.udp.label}] ⚠️ 摄像头 {self.cam_id} 读取失败")
                time.sleep(0.01)
                continue

            # 跳帧 (减负载)
            skip_counter += 1
            if skip_counter <= FRAME_SKIP:
                continue
            skip_counter = 0

            # 水平翻转 (Python 端做 mirror)
            if self.flip_horizontal:
                frame = cv2.flip(frame, 1)

            # 转 RGB
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            rgb.flags.writeable = False

            # MediaPipe 推理
            results = self.pose.process(rgb)

            rgb.flags.writeable = True
            frame = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)

            # 提取 landmarks → 发送 UDP
            if results.pose_landmarks:
                lm_dict = {}

                for idx, lm in enumerate(results.pose_landmarks.landmark):
                    # k 是 MediaPipe key (0..32)
                    # 发送原始归一化坐标 (0-1) + z

                    # 如果 Python 端做了 flip，x 坐标也需要镜像(1-x)，
                    # 这样 Unity 端就不用再做一次 mirror
                    x = lm.x
                    if self.flip_horizontal:
                        x = 1.0 - x

                    lm_dict[str(idx)] = {
                        "x": x,
                        "y": lm.y,
                        "z": lm.z,
                        "visibility": lm.visibility,
                    }

                self.udp.send_landmarks(lm_dict)

                # 画面中画骨架
                if SHOW_WINDOWS:
                    self.mp_draw.draw_landmarks(
                        frame,
                        results.pose_landmarks,
                        self.mp_pose.POSE_CONNECTIONS,
                        landmark_drawing_spec=self.mp_style.get_default_pose_landmarks_style(),
                    )

            # FPS
            self.frame_count += 1
            self.frame_times.append(time.time())

            if SHOW_WINDOWS:
                self._draw_hud(frame)
                cv2.imshow(self.window_title, frame)
                key = cv2.waitKey(1) & 0xFF
                if key == 27:  # ESC
                    self.running = False

    def _draw_hud(self, frame):
        fps = self.get_fps()
        udp_fps = self.udp.get_fps()
        h, w = frame.shape[:2]

        # 半透明底条
        overlay = frame.copy()
        cv2.rectangle(overlay, (0, 0), (w, 30), (0, 0, 0), -1)
        frame[:] = cv2.addWeighted(frame, 0.7, overlay, 0.3, 0)

        cv2.putText(frame,
                    f"FPS: {fps:.0f}  UDP: {udp_fps:.0f}fps  Port: {self.udp.addr[1]}  Frame: {self.frame_count}",
                    (6, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 1)

    def get_fps(self):
        if len(self.frame_times) < 2:
            return 0.0
        dt = self.frame_times[-1] - self.frame_times[0]
        return (len(self.frame_times) - 1) / dt if dt > 0 else 0.0

    def stop(self):
        self.running = False
        if self.thread:
            self.thread.join(timeout=2)
        if self.cap:
            self.cap.release()
        self.pose.close()
        print(f"[{self.udp.label}] 摄像头 {self.cam_id} 已停止 (共 {self.frame_count} 帧)")

# ═══════════════════════════════════════════════════════════════
# 主入口
# ═══════════════════════════════════════════════════════════════

def main():
    print("=" * 55)
    print("  双摄像头 MediaPipe Pose → UDP")
    print("  前摄(正面) → :8888    左摄(侧面) → :8889")
    print("=" * 55)

    # ── 前摄 (正面) ──
    front_udp = UdpSender(FRONT_UDP_IP, FRONT_UDP_PORT, "前摄")
    front_cam = CameraProcessor(
        cam_id=FRONT_CAM_ID,
        udp_sender=front_udp,
        window_title=FRONT_WINDOW_TITLE,
        flip_horizontal=True,   # 前摄镜像翻转 (像照镜子)
    )

    # ── 左摄 (侧面) ──
    left_udp = UdpSender(LEFT_UDP_IP, LEFT_UDP_PORT, "左摄")
    left_cam = CameraProcessor(
        cam_id=LEFT_CAM_ID,
        udp_sender=left_udp,
        window_title=LEFT_WINDOW_TITLE,
        flip_horizontal=False,  # 侧面一般不翻转，保持原始方向
    )

    # 启动
    if not front_cam.start():
        print("❌ 前摄启动失败，退出")
        sys.exit(1)

    # 左摄是可选的 — 没有第二个摄像头就单摄跑
    left_ok = left_cam.start()
    if not left_ok:
        print("⚠️ 左摄未找到，仅前摄运行 (Unity 端将回退到单摄Z深度模式)")
        print("   按 Ctrl+C 退出")

    print()
    print("  运行中...")
    print("  ┌──────────┬────────┬──────────┐")
    print("  │ 摄像头   │ 位置   │ UDP Port │")
    print("  ├──────────┼────────┼──────────┤")
    print(f"  │ 前摄({FRONT_CAM_ID})  │ 正面   │ {FRONT_UDP_PORT}      │")
    if left_ok:
        print(f"  │ 左摄({LEFT_CAM_ID})  │ 左侧面 │ {LEFT_UDP_PORT}      │")
    print("  └──────────┴────────┴──────────┘")
    print("  按 Ctrl+C 或关闭窗口退出")
    print()

    try:
        while front_cam.running:
            if left_ok and not left_cam.running:
                print("⚠️ 左摄已中断")
                left_ok = False
            time.sleep(0.5)

    except KeyboardInterrupt:
        print("\n⏹ 用户中断")

    finally:
        print("\n正在关闭...")
        front_cam.stop()
        if left_ok:
            left_cam.stop()
        front_udp.close()
        if left_ok:
            left_udp.close()
        cv2.destroyAllWindows()
        print("已退出")

if __name__ == "__main__":
    main()
