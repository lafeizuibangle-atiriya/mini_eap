# -*- coding: utf-8 -*-
"""Mini-EAP 设备模拟器
模拟 2 台半导体机台（ETCH01 刻蚀 / CVD01 沉积）：
- 状态机：IDLE -> LOADING -> PROCESSING -> UNLOADING -> IDLE；异常时进入 ALARM / PAUSED
- 工艺参数（腔室温度 / 腔压）随机漂移，温度连续超限 3 个周期触发报警
- 通过 TCP 暴露 JSON 行协议（每行一个 JSON），供 C# WinForm 主控端连接
- 支持命令：START / STOP / PAUSE / RESET / GET_STATUS / LOAD_RECIPE

运行：python device_simulator.py --port 9000
仅用 Python 标准库，无第三方依赖。
"""
import argparse
import json
import random
import socket
import sys
import threading
import time

# 兼容 Windows GBK 控制台
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

TICK_SECONDS = 2.0          # 状态上报周期
TEMP_LIMIT = 85.0           # 温度报警阈值
ALARM_CONSECUTIVE = 3       # 连续超限次数 -> 报警
ALARM_CODE = 'E1024'
ALARM_TEXT = '腔室温度超限'
ALARM_LEVEL = 'HIGH'


class Device:
    """一台机台的模拟：状态机 + 参数漂移 + 报警逻辑"""

    def __init__(self, eqp_id, name):
        self.eqp_id = eqp_id
        self.name = name
        self.state = 'IDLE'
        self.recipe = None
        self.progress = 0
        self.chamber_temp = 60.0 + random.random() * 5
        self.pressure = round(random.uniform(0.3, 0.6), 3)
        self.high_temp_count = 0
        self.alarm_active = False
        self.lock = threading.Lock()

    def snapshot(self):
        with self.lock:
            return {
                'type': 'status',
                'device': self.eqp_id,
                'name': self.name,
                'state': self.state,
                'recipe': self.recipe or '',
                'progress': self.progress,
                'chamber_temp': round(self.chamber_temp, 1),
                'pressure': self.pressure,
                'ts': int(time.time()),
            }

    # ---------- 状态机推进（每个周期调用一次） ----------
    def tick(self, send_event):
        with self.lock:
            # 参数随机漂移
            self.chamber_temp += random.uniform(-1.5, 2.0)
            if self.state != 'PROCESSING':
                # 非工艺状态向基准温度回落
                self.chamber_temp += (62.0 - self.chamber_temp) * 0.3
            self.chamber_temp = max(20.0, min(100.0, self.chamber_temp))
            self.pressure = round(max(0.1, min(0.9, self.pressure + random.uniform(-0.02, 0.02))), 3)

            # 报警判定：连续超限
            if self.chamber_temp > TEMP_LIMIT:
                self.high_temp_count += 1
            else:
                self.high_temp_count = 0

            if self.high_temp_count >= ALARM_CONSECUTIVE and not self.alarm_active:
                self.alarm_active = True
                self.state = 'ALARM' if self.state in ('PROCESSING', 'LOADING') else self.state
                send_event({
                    'type': 'alarm',
                    'device': self.eqp_id,
                    'code': ALARM_CODE,
                    'text': ALARM_TEXT,
                    'level': ALARM_LEVEL,
                    'value': round(self.chamber_temp, 1),
                    'ts': int(time.time()),
                })

            # 状态推进
            if self.state == 'LOADING':
                self.progress = 0
                self.state = 'PROCESSING'
            elif self.state == 'PROCESSING':
                self.progress = min(100, self.progress + random.randint(8, 20))
                if self.progress >= 100:
                    self.state = 'UNLOADING'
            elif self.state == 'UNLOADING':
                send_event({
                    'type': 'run_done',
                    'device': self.eqp_id,
                    'recipe': self.recipe or '',
                    'result': 'OK',
                    'ts': int(time.time()),
                })
                self.state = 'IDLE'
                self.progress = 0

    # ---------- 命令处理 ----------
    def handle(self, cmd, payload, send_event):
        with self.lock:
            if cmd == 'GET_STATUS':
                return True, '状态已上报'
            if cmd == 'LOAD_RECIPE':
                if self.state != 'IDLE':
                    return False, f'机台 {self.state} 状态下不允许加载 Recipe'
                self.recipe = (payload or {}).get('recipe', '')
                if not self.recipe:
                    return False, '缺少 recipe 字段'
                return True, f'Recipe {self.recipe} 已加载'
            if self.alarm_active and cmd not in ('RESET',):
                return False, '机台处于 ALARM 状态，请先 RESET'
            if cmd == 'START':
                if self.state != 'IDLE':
                    return False, f'机台 {self.state} 状态下不允许 START'
                if not self.recipe:
                    return False, '未加载 Recipe，请先 LOAD_RECIPE'
                self.state = 'LOADING'
                return True, f'开始执行 {self.recipe}'
            if cmd == 'PAUSE':
                if self.state != 'PROCESSING':
                    return False, f'机台 {self.state} 状态下不允许 PAUSE'
                self.state = 'PAUSED'
                return True, '已暂停'
            if cmd == 'STOP':
                if self.state in ('IDLE',):
                    return False, '机台已在 IDLE'
                self.state = 'IDLE'
                self.progress = 0
                send_event({
                    'type': 'run_done',
                    'device': self.eqp_id,
                    'recipe': self.recipe or '',
                    'result': 'ABORT',
                    'ts': int(time.time()),
                })
                return True, '已停止'
            if cmd == 'RESET':
                self.alarm_active = False
                self.high_temp_count = 0
                self.state = 'IDLE'
                self.progress = 0
                send_event({
                    'type': 'alarm_clear',
                    'device': self.eqp_id,
                    'code': ALARM_CODE,
                    'ts': int(time.time()),
                })
                return True, '报警已复位'
            return False, f'未知命令 {cmd}'


class SimulatorServer:
    """TCP 服务：接受主控端连接，广播状态事件，处理命令"""

    def __init__(self, port, devices):
        self.port = port
        self.devices = devices
        self.clients = []          # 已连接的 socket 列表
        self.clients_lock = threading.Lock()

    def broadcast(self, obj):
        line = (json.dumps(obj, ensure_ascii=False) + '\n').encode('utf-8')
        with self.clients_lock:
            dead = []
            for sock in self.clients:
                try:
                    sock.sendall(line)
                except OSError:
                    dead.append(sock)
            for sock in dead:
                self.clients.remove(sock)

    def device_loop(self, device):
        while True:
            device.tick(self.broadcast)
            self.broadcast(device.snapshot())
            time.sleep(TICK_SECONDS)

    def handle_client(self, conn, addr):
        print(f'[+] 主控端接入: {addr}')
        with self.clients_lock:
            self.clients.append(conn)
        buf = b''
        try:
            while True:
                data = conn.recv(4096)
                if not data:
                    break
                buf += data
                while b'\n' in buf:
                    line, buf = buf.split(b'\n', 1)
                    if not line.strip():
                        continue
                    self.process_command(conn, line)
        except OSError:
            pass
        finally:
            with self.clients_lock:
                if conn in self.clients:
                    self.clients.remove(conn)
            print(f'[-] 主控端断开: {addr}')

    def process_command(self, conn, line):
        try:
            msg = json.loads(line.decode('utf-8'))
        except (ValueError, UnicodeDecodeError):
            self.reply(conn, {'type': 'ack', 'ok': False, 'msg': 'JSON 解析失败'})
            return
        cmd = msg.get('cmd', '')
        device_id = msg.get('device', '')
        device = next((d for d in self.devices if d.eqp_id == device_id), None)
        if device is None:
            self.reply(conn, {'type': 'ack', 'cmd': cmd, 'device': device_id, 'ok': False, 'msg': f'未知设备 {device_id}'})
            return
        ok, text = device.handle(cmd, msg, self.broadcast)
        self.reply(conn, {'type': 'ack', 'cmd': cmd, 'device': device_id, 'ok': ok, 'msg': text})
        print(f'[cmd] {device_id} {cmd} -> {"OK" if ok else text}')

    def reply(self, conn, obj):
        try:
            conn.sendall((json.dumps(obj, ensure_ascii=False) + '\n').encode('utf-8'))
        except OSError:
            pass

    def serve_forever(self):
        for d in self.devices:
            t = threading.Thread(target=self.device_loop, args=(d,), daemon=True)
            t.start()
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(('0.0.0.0', self.port))
        srv.listen(5)
        print(f'Mini-EAP 设备模拟器已启动，监听端口 {self.port}')
        print(f'机台: {", ".join(d.eqp_id for d in self.devices)}')
        while True:
            conn, addr = srv.accept()
            threading.Thread(target=self.handle_client, args=(conn, addr), daemon=True).start()


def main():
    parser = argparse.ArgumentParser(description='Mini-EAP 设备模拟器')
    parser.add_argument('--port', type=int, default=9000, help='TCP 监听端口，默认 9000')
    args = parser.parse_args()

    devices = [
        Device('ETCH01', '刻蚀机 1 号'),
        Device('CVD01', '沉积机 1 号'),
    ]
    SimulatorServer(args.port, devices).serve_forever()


if __name__ == '__main__':
    main()
