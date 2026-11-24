-- Mini-EAP 存储过程：业务与报表操作走存储过程（JD 明确要求）
-- 执行：mysql -u root -p --default-character-set=utf8mb4 < 02_procedures.sql
USE mini_eap;

DELIMITER $$

-- 记录一条报警
DROP PROCEDURE IF EXISTS sp_add_alarm $$
CREATE PROCEDURE sp_add_alarm (
    IN p_eqp_id    VARCHAR(32),
    IN p_code      VARCHAR(16),
    IN p_text      VARCHAR(128),
    IN p_level     VARCHAR(8),
    IN p_value     DECIMAL(10,1)
)
BEGIN
    INSERT INTO alarm (eqp_id, alarm_code, alarm_text, level, value)
    VALUES (p_eqp_id, p_code, p_text, p_level, p_value);
END $$

-- 报警复位
DROP PROCEDURE IF EXISTS sp_clear_alarm $$
CREATE PROCEDURE sp_clear_alarm (
    IN p_eqp_id    VARCHAR(32),
    IN p_code      VARCHAR(16)
)
BEGIN
    UPDATE alarm
       SET clear_time = NOW()
     WHERE eqp_id = p_eqp_id
       AND alarm_code = p_code
       AND clear_time IS NULL;
END $$

-- 记录一次运行（结束时调用）
DROP PROCEDURE IF EXISTS sp_finish_run $$
CREATE PROCEDURE sp_finish_run (
    IN p_run_id     INT,
    IN p_result     VARCHAR(16)
)
BEGIN
    UPDATE run_history
       SET end_time = NOW(),
           result = p_result,
           duration_sec = TIMESTAMPDIFF(SECOND, start_time, NOW())
     WHERE id = p_run_id;
END $$

-- 报警统计报表：按机台汇总发生次数与平均复位时长
DROP PROCEDURE IF EXISTS sp_report_alarm_stats $$
CREATE PROCEDURE sp_report_alarm_stats (
    IN p_begin DATE,
    IN p_end   DATE
)
BEGIN
    SELECT eqp_id,
           alarm_code,
           COUNT(*)              AS occur_count,
           MAX(value)            AS max_value,
           AVG(TIMESTAMPDIFF(MINUTE, occur_time, clear_time)) AS avg_clear_minutes
      FROM alarm
     WHERE occur_time BETWEEN p_begin AND DATE_ADD(p_end, INTERVAL 1 DAY)
     GROUP BY eqp_id, alarm_code
     ORDER BY occur_count DESC;
END $$

-- 生产日报：按机台统计运行次数 / 完成率 / 平均时长
DROP PROCEDURE IF EXISTS sp_report_daily_run $$
CREATE PROCEDURE sp_report_daily_run (
    IN p_eqp_id VARCHAR(32),
    IN p_day    DATE
)
BEGIN
    SELECT recipe_name,
           COUNT(*)                                            AS run_count,
           SUM(CASE WHEN result = 'OK' THEN 1 ELSE 0 END)      AS ok_count,
           ROUND(AVG(duration_sec) / 60.0, 1)                  AS avg_minutes
      FROM run_history
     WHERE eqp_id = p_eqp_id
       AND start_time BETWEEN p_day AND DATE_ADD(p_day, INTERVAL 1 DAY)
       AND end_time IS NOT NULL
     GROUP BY recipe_name;
END $$

DELIMITER ;
