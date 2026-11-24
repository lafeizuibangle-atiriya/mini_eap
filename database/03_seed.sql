-- Mini-EAP 演示数据
-- 执行：mysql -u root -p --default-character-set=utf8mb4 < 03_seed.sql
USE mini_eap;

INSERT INTO equipment (eqp_id, eqp_name, model, status) VALUES
('ETCH01', '刻蚀机 1 号', 'ETCH-200i', 'IDLE'),
('CVD01',  '沉积机 1 号', 'CVD-300',  'IDLE');

INSERT INTO recipe (recipe_name, version, step_count, content) VALUES
('ETCH_STD_01', 'V1.2', 5, '{"steps":[1,2,3,4,5],"temp_max":85,"pressure":0.45}'),
('CVD_LOWK_02', 'V1.0', 7, '{"steps":[1,2,3,4,5,6,7],"temp_max":80,"pressure":0.30}');

INSERT INTO run_history (eqp_id, recipe_name, lot_id, start_time, end_time, result, duration_sec) VALUES
('ETCH01', 'ETCH_STD_01', 'LOT-A2301', NOW() - INTERVAL 3 HOUR, NOW() - INTERVAL 155 MINUTE, 'OK',    25),
('ETCH01', 'ETCH_STD_01', 'LOT-A2302', NOW() - INTERVAL 2 HOUR, NOW() - INTERVAL 95  MINUTE, 'OK',    25),
('CVD01',  'CVD_LOWK_02', 'LOT-B1108', NOW() - INTERVAL 4 HOUR, NOW() - INTERVAL 200 MINUTE, 'OK',    40),
('CVD01',  'CVD_LOWK_02', 'LOT-B1109', NOW() - INTERVAL 1 HOUR, NOW() - INTERVAL 40  MINUTE, 'ABORT', 20);

INSERT INTO alarm (eqp_id, alarm_code, alarm_text, level, value, occur_time, clear_time) VALUES
('ETCH01', 'E1024', '腔室温度超限', 'HIGH', 87.5, NOW() - INTERVAL 5 HOUR, NOW() - INTERVAL 295 MINUTE),
('CVD01',  'E1024', '腔室温度超限', 'HIGH', 86.1, NOW() - INTERVAL 6 HOUR, NOW() - INTERVAL 350 MINUTE);
