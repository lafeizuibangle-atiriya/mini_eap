-- Mini-EAP 数据库初始化：建库建表
-- 执行：mysql -u root -p --default-character-set=utf8mb4 < 01_init.sql
-- SQL 保持标准写法，便于后续迁移 Oracle

DROP DATABASE IF EXISTS mini_eap;
CREATE DATABASE mini_eap DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
USE mini_eap;

-- 设备台账
CREATE TABLE equipment (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    eqp_id      VARCHAR(32)  NOT NULL UNIQUE COMMENT '机台编号，如 ETCH01',
    eqp_name    VARCHAR(64)  NOT NULL COMMENT '机台名称',
    model       VARCHAR(64)  COMMENT '机台型号',
    status      VARCHAR(16)  NOT NULL DEFAULT 'IDLE' COMMENT '最新状态',
    updated_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) COMMENT '设备台账';

-- Recipe（工艺程序）管理
CREATE TABLE recipe (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    recipe_name VARCHAR(64)  NOT NULL COMMENT '工艺程序名',
    version     VARCHAR(16)  NOT NULL DEFAULT 'V1.0' COMMENT '版本',
    step_count  INT          NOT NULL DEFAULT 1 COMMENT '工艺步骤数',
    content     TEXT         COMMENT '参数配置（JSON 文本）',
    enabled     TINYINT      NOT NULL DEFAULT 1,
    created_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uk_recipe (recipe_name, version)
) COMMENT 'Recipe 工艺程序';

-- 运行历史（track data 汇总）
CREATE TABLE run_history (
    id            INT AUTO_INCREMENT PRIMARY KEY,
    eqp_id        VARCHAR(32)  NOT NULL,
    recipe_name   VARCHAR(64)  NOT NULL,
    lot_id        VARCHAR(32)  COMMENT '批次号',
    start_time    DATETIME     NOT NULL,
    end_time      DATETIME     COMMENT 'NULL 表示仍在运行',
    result        VARCHAR(16)  COMMENT 'OK / ABORT / NG',
    duration_sec  INT          COMMENT '运行秒数',
    KEY idx_eqp_time (eqp_id, start_time)
) COMMENT '设备运行历史';

-- 报警记录
CREATE TABLE alarm (
    id           INT AUTO_INCREMENT PRIMARY KEY,
    eqp_id       VARCHAR(32)  NOT NULL,
    alarm_code   VARCHAR(16)  NOT NULL,
    alarm_text   VARCHAR(128) NOT NULL,
    level        VARCHAR(8)   NOT NULL DEFAULT 'INFO' COMMENT 'INFO / WARN / HIGH',
    value        DECIMAL(10,1) COMMENT '触发时的参数值',
    occur_time   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    clear_time   DATETIME     COMMENT 'NULL 表示未复位',
    confirmed    TINYINT      NOT NULL DEFAULT 0 COMMENT '是否已确认',
    KEY idx_eqp_time (eqp_id, occur_time)
) COMMENT '报警记录';
