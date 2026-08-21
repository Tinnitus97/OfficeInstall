-- ---------------------------------------------------------------------------
-- Time Billing plugin – reference schema
--
-- This file documents the catalogue table created by the plugin. The table is
-- created automatically by BillingPlugin::install(); you only need to run this
-- manually if you install the schema by hand. Replace %PREFIX% with your
-- configured table prefix (default: ost_).
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS `%PREFIX%billing_time_type` (
  `id`          int(11) unsigned NOT NULL AUTO_INCREMENT,
  `name`        varchar(191) NOT NULL DEFAULT '',
  `hourly_rate` decimal(12,2) NOT NULL DEFAULT '0.00',
  `billable`    tinyint(1) unsigned NOT NULL DEFAULT '1',
  `isdefault`   tinyint(1) unsigned NOT NULL DEFAULT '0',
  `sort`        int(11) unsigned NOT NULL DEFAULT '0',
  `isactive`    tinyint(1) unsigned NOT NULL DEFAULT '1',
  `created`     datetime NOT NULL,
  `updated`     datetime NOT NULL,
  PRIMARY KEY (`id`)
) DEFAULT CHARSET=utf8;

-- Default time type – matches the default `time_type_id` = 1 written by the
-- Time Recording plugin, so existing time entries are billed as "Standard".
INSERT INTO `%PREFIX%billing_time_type`
  (`id`, `name`, `hourly_rate`, `billable`, `isdefault`, `sort`, `isactive`, `created`, `updated`)
VALUES
  (1, 'Standard', 0.00, 1, 1, 0, 1, NOW(), NOW());
