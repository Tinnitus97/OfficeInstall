<?php
/*********************************************************************
    billing.php

    Time Billing plugin – main class.

    Registers a "Billing" application in the staff control panel and wires
    up the routes that render billing reports and invoices from the time
    entries recorded by the Time Recording plugin (table ost_timesheet).

    PHP 8.4 compatible. Requires osTicket v1.18+.

    NOTE ON LOADING ORDER
    ---------------------
    osTicket flags a plugin as "defunct — missing" whenever it includes this
    file and the BillingPlugin class is *not* defined afterwards. To make that
    impossible, this file declares the class using ONLY the Plugin base class
    (which osTicket guarantees is loaded before it includes us). Every other
    dependency is loaded either lazily inside bootstrap()/handlers, or at the
    very BOTTOM of this file — after the class already exists — so that a
    problem in any dependency can never prevent the class from being defined.
**********************************************************************/

if (!defined('INCLUDE_DIR')) die('Access Denied');

define('PLUGIN_BILLING_SCHEMA', '6');   // + own thread event type
define('PLUGIN_BILLING_EVENT', 'billing');   // bump to force a schema refresh

class BillingPlugin extends Plugin {

    var $config_class = 'BillingConfig';

    function isMultiInstance() {
        return false;
    }

    /**
     * Return the configuration of the (single) active instance.
     */
    /**
     * Lightweight self-diagnostics. The plugin does very little on a save, but
     * "ticket creation is slow" is hard to attribute without numbers. These
     * two helpers measure the time spent INSIDE the plugin's own handlers and
     * write a single line to the PHP error log when it is actually slow
     * (> 250 ms). Nothing is logged on a healthy system, so this costs
     * effectively nothing - but if the plugin ever is the bottleneck, it says
     * so in plain text instead of leaving us guessing.
     */
    private static $bTimers = array();
    /** Diagnostics are opt-in - while off, nothing is measured or stored. */
    private static $bDiagOn = false;
    static function diagEnabled() { return self::$bDiagOn; }
    static function timeStart($key) {
        if (!self::$bDiagOn) return;
        self::$bTimers[$key] = microtime(true);
    }
    /**
     * Record a MARKER: how far into the request we are at this point. Two
     * markers around osTicket's own creation phases localise where the time is
     * actually spent (bootstrap/validation vs. inside Ticket::open, e.g.
     * outbound e-mail).
     */
    static function mark($label) {
        if (!self::$bDiagOn) return;
        $reqMs = isset($_SERVER['REQUEST_TIME_FLOAT'])
            ? (microtime(true) - (float) $_SERVER['REQUEST_TIME_FLOAT']) * 1000 : 0;
        if (session_status() !== PHP_SESSION_ACTIVE)
            return;
        if (!isset($_SESSION['billing_perf']) || !is_array($_SESSION['billing_perf']))
            $_SESSION['billing_perf'] = array();
        $_SESSION['billing_perf'][] = array(
            'when'   => date('Y-m-d H:i:s'),
            'key'    => $label,
            'ms'     => 0.0,
            'req_ms' => round($reqMs, 1),
            'method' => $_SERVER['REQUEST_METHOD'] ?? '?',
            'uri'    => $_SERVER['REQUEST_URI'] ?? '?',
        );
        if (count($_SESSION['billing_perf']) > 20)
            $_SESSION['billing_perf'] = array_slice($_SESSION['billing_perf'], -20);
    }

    static function timeEnd($key) {
        if (!self::$bDiagOn) return;
        if (!isset(self::$bTimers[$key]))
            return;
        $ms = (microtime(true) - self::$bTimers[$key]) * 1000;
        unset(self::$bTimers[$key]);
        // How long has the WHOLE request been running so far? That is the
        // decisive comparison: if the plugin needs 20 ms but the request
        // already burned 8 seconds, the delay is demonstrably elsewhere.
        $reqMs = isset($_SERVER['REQUEST_TIME_FLOAT'])
            ? (microtime(true) - (float) $_SERVER['REQUEST_TIME_FLOAT']) * 1000 : 0;

        // ALWAYS record the measurement in the session (last 20). The billing
        // module has a "Diagnostics" page that shows these, so the numbers are
        // reachable even when neither the PHP error log nor osTicket's system
        // log is available/enabled - which is exactly the situation on many
        // IIS setups.
        if (session_status() === PHP_SESSION_ACTIVE) {
            if (!isset($_SESSION['billing_perf']) || !is_array($_SESSION['billing_perf']))
                $_SESSION['billing_perf'] = array();
            $_SESSION['billing_perf'][] = array(
                'when'   => date('Y-m-d H:i:s'),
                'key'    => $key,
                'ms'     => round($ms, 1),
                'req_ms' => round($reqMs, 1),
                'method' => $_SERVER['REQUEST_METHOD'] ?? '?',
                'uri'    => $_SERVER['REQUEST_URI'] ?? '?',
            );
            if (count($_SESSION['billing_perf']) > 20)
                $_SESSION['billing_perf'] = array_slice($_SESSION['billing_perf'], -20);
        }

        // Report if the plugin itself is slow, OR if the request as a whole is
        // slow (so the log still shows who is to blame when we are not).
        if ($ms <= 250 && $reqMs <= 3000)
            return;
        $msg = sprintf('Billing handler "%s": %.0f ms | whole request so far: %.0f ms | %s %s',
            $key, $ms, $reqMs,
            $_SERVER['REQUEST_METHOD'] ?? '?',
            $_SERVER['REQUEST_URI'] ?? '?');
        // Prefer osTicket's own system log - it is visible under
        // Admin Panel > Dashboard > System Logs, so no access to the PHP
        // error log (which is often not configured at all on IIS) is needed.
        // alert=false so this never triggers warning e-mails.
        global $ost;
        if ($ost && method_exists($ost, 'logWarning')) {
            try {
                $ost->logWarning('Billing: slow handler', $msg, false);
                return;
            } catch (Throwable $e) {
                // fall through to the plain error log
            }
        }
        error_log('[billing] '.$msg);
    }

    function getPluginConfig($reload = false) {
        static $config = null;
        if ($config === null || $reload) {
            if (($inst = $this->getActiveInstances()->first()))
                $config = $inst->getConfig();
        }
        return $config;
    }

    /**
     * Runs on every load of an active plugin.
     *
     * IMPORTANT: parent::__onload() populates $this->info from plugin.php.
     * Without it, osTicket's plugins page sees an empty info array on this
     * impl instance and flags the plugin as "defunct — missing".
     *
     * RE-ENTRANCY: anything here that queries related rows (the plugin
     * instances) can make the ORM hydrate this very model again, which
     * re-fires __onload -> infinite recursion -> memory exhaustion. The
     * static guard below breaks that cycle for ALL BillingPlugin objects
     * in the request (a static inside a method is shared across instances).
     */
    function __onload() {
        parent::__onload();

        static $busy = false;
        if ($busy)
            return;
        // Housekeeping (schema checks, instance auto-creation, seeding) has no
        // business running while something is being SAVED. Doing it on every
        // POST added database round-trips to each ticket create/reply for no
        // benefit - it only ever needs to happen on a normal page load.
        if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'GET')
            return;
        $busy = true;
        try {
            // Schema maintenance is expensive: SHOW TABLES / SHOW COLUMNS /
            // SHOW INDEX on every single request, and if an ALTER ever fails
            // it would be retried forever - on a large timesheet table that
            // alone made saving a ticket crawl. Do it once per schema version
            // and then skip it entirely for all later requests.
            $done = false;
            try {
                $cfg = $this->getPluginConfig();
                $done = $cfg && (string) $cfg->get('schema_done') === (string) PLUGIN_BILLING_SCHEMA;
            } catch (Throwable $e) {
                $done = false;
            }
            if (!$done) {
                try {
                    $this->ensureSchema();
                } catch (Throwable $e) {
                    // schema maintenance must never block the rest of loading
                }
                try {
                    $this->seedConfigDefaults();
                } catch (Throwable $e) {
                    // seeding the default footer texts is best-effort
                }
                try {
                    if ($cfg = $this->getPluginConfig())
                        $cfg->set('schema_done', (string) PLUGIN_BILLING_SCHEMA);
                } catch (Throwable $e) {
                    // if we cannot remember it, we simply check again next time
                }
            }
            try {
                $this->ensureInstance();
            } catch (Throwable $e) {
                // instance auto-creation is best-effort
            }
        } finally {
            $busy = false;
        }
    }

    /**
     * Non-multi-instance plugin: make sure exactly one enabled instance
     * exists so the configuration is reachable right after installation
     * (mirrors the Time Recording plugin's behaviour).
     */
    /**
     * Write the old-version summary texts into the stored configuration once,
     * so the report/PDF show them out of the box (left/right below the table)
     * without the admin having to open the settings. Guarded by a flag; if the
     * admin later CLEARS a field, the empty value is kept (empty -> prints
     * nothing) and never re-seeded.
     */
    private function seedConfigDefaults() {
        $cfg = $this->getPluginConfig();
        if (!$cfg)
            return;
        if ($cfg->get('summary_seeded') === '2')
            return;
        // Use the HELPDESK'S configured default language for these seeded
        // defaults (not whichever staff session happens to trigger the seed
        // first) so every admin sees the same out-of-the-box text regardless
        // of their own interface language.
        $__ = $this->helpdeskTranslator();
        if (trim((string) $cfg->get('table_footer_left')) === '')
            $cfg->set('table_footer_left',
                $__('Total tickets: %{report.count}, trips: %{report.trips}'));
        if (trim((string) $cfg->get('table_footer_right')) === '')
            $cfg->set('table_footer_right', $__('Billable time: %{report.billable}'));
        $cfg->set('summary_seeded', '2');
    }

    /**
     * A translator function bound to the helpdesk's configured DEFAULT
     * language (Admin Panel > Settings > System > Default), independent of
     * the current staff session's own interface language. Falls back to the
     * plain string (English) if the primary language can't be determined.
     */
    private function helpdeskTranslator() {
        global $cfg;
        $locale = ($cfg && method_exists($cfg, 'getPrimaryLanguage'))
            ? (string) $cfg->getPrimaryLanguage() : 'en_US';
        // Read our own translation table directly. Going through gettext here
        // proved unreliable (the domain is not necessarily bound to the
        // helpdesk language at bootstrap time), which is why the seeded
        // defaults came out English on a German helpdesk.
        $codes = array();
        $locale = str_replace('-', '_', $locale);
        if ($locale !== '') {
            $codes[] = $locale;                       // de_DE
            $codes[] = strtolower($locale);           // de_de
            $short = substr($locale, 0, 2);
            if ($short !== '') $codes[] = strtolower($short);   // de
        }
        $table = null;
        foreach ($codes as $code) {
            $file = dirname(__file__).'/i18n/'.$code.'/LC_MESSAGES/billing.mo.php';
            if (@file_exists($file)) {
                $data = @include $file;
                if (is_array($data)) { $table = $data; break; }
            }
        }
        if (!is_array($table))
            return function ($msgid) { return $msgid; };
        return function ($msgid) use ($table) {
            return isset($table[$msgid]) ? $table[$msgid] : $msgid;
        };
    }

    private function ensureInstance() {
        static $done = false;
        if ($done)
            return;
        $done = true;

        if (!$this->getNumInstances()) {
            list($__, $_N) = self::translate('billing');
            $errors = array();
            $this->addInstance(array('name' => $__('Billing')), $errors);
        }
        if (($i = $this->getInstances()->first()) && !$i->isEnabled()) {
            $i->setStatus(1);
            $i->save(true);
        }
    }

    /**
     * Create the time-type catalogue table and seed the default type.
     *
     * Deliberately uses NO plugin config and NO instance lookups (see the
     * re-entrancy note on __onload). One cheap SHOW TABLES per request,
     * guarded by a static.
     */
    /**
     * Add columns introduced after the first release to an existing table.
     * Safe to call repeatedly - each column is only added when missing.
     */
    /**
     * Add billed/billed_at/is_goodwill to an existing billing_ticket_type
     * table (older installs only had ticket_id/time_type_id/updated).
     */
    private function ensureTicketTypeColumns($table) {
        // Make sure the table itself exists before touching its columns.
        // ensureSchema() only issued the CREATE in the branch where the
        // time-type CATALOGUE did not yet exist. On installs whose catalogue
        // predated this per-ticket table (i.e. most upgrades), that branch is
        // never taken, so the table was never created - and every
        // setTicketType() INSERT then failed silently while getTicketType()
        // found nothing, so the ticket's type always fell back to the default
        // ("Normal") on view. Creating it here (idempotent) closes that gap.
        db_query("CREATE TABLE IF NOT EXISTS `$table` (
            `ticket_id`    int(11) unsigned NOT NULL,
            `time_type_id` int(11) unsigned NOT NULL DEFAULT '1',
            `billed`       tinyint(1) unsigned NOT NULL DEFAULT '0',
            `billed_at`    datetime NULL DEFAULT NULL,
            `is_goodwill`  tinyint(1) unsigned NOT NULL DEFAULT '0',
            `trips`        int(10) unsigned NOT NULL DEFAULT '0',
            `updated`      datetime NOT NULL,
            PRIMARY KEY (`ticket_id`)
        ) DEFAULT CHARSET=utf8;", false);

        $have = array();
        if (($c = db_query("SHOW COLUMNS FROM `$table`", false)))
            while (($r = db_fetch_array($c)))
                $have[$r['Field']] = true;
        $add = array(
            'billed'      => "ALTER TABLE `$table` ADD COLUMN `billed` tinyint(1) unsigned "
                            . "NOT NULL DEFAULT '0' AFTER `time_type_id`",
            'billed_at'   => "ALTER TABLE `$table` ADD COLUMN `billed_at` datetime NULL "
                            . "DEFAULT NULL AFTER `billed`",
            'is_goodwill' => "ALTER TABLE `$table` ADD COLUMN `is_goodwill` tinyint(1) unsigned "
                            . "NOT NULL DEFAULT '0' AFTER `billed_at`",
            'trips'       => "ALTER TABLE `$table` ADD COLUMN `trips` int(10) unsigned "
                            . "NOT NULL DEFAULT '0' AFTER `is_goodwill`",
        );
        foreach ($add as $col => $sql)
            if (empty($have[$col]))
                db_query($sql, false);
    }

    private function ensureColumns($table) {
        // one query lists every column; add whichever ones are missing
        $have = array();
        if (($c = db_query("SHOW COLUMNS FROM `$table`", false)))
            while (($r = db_fetch_array($c)))
                $have[$r['Field']] = true;
        $add = array(
            'factor' => "ALTER TABLE `$table` ADD COLUMN `factor` smallint(5) unsigned "
                      . "NOT NULL DEFAULT '100' AFTER `billable`",
            'onsite' => "ALTER TABLE `$table` ADD COLUMN `onsite` tinyint(1) unsigned "
                      . "NOT NULL DEFAULT '0' AFTER `factor`",
            'travel_fee' => "ALTER TABLE `$table` ADD COLUMN `travel_fee` decimal(12,2) "
                      . "NOT NULL DEFAULT '0.00' AFTER `onsite`",
        );
        foreach ($add as $col => $sql)
            if (empty($have[$col]))
                db_query($sql, false);
    }

    /**
     * Every billing query and the ticket-type UPDATE filter the timesheet by
     * (object_type, object_id). Without an index MySQL scans the whole table,
     * which makes both reporting and ticket creation slow once many time
     * entries exist. Add the index once if the Time Recording table lacks it.
     */
    private function ensureTimesheetIndex() {
        if (!Billing::timesheetAvailable())
            return;
        $table = BILLING_TIMESHEET_TABLE;
        $res = db_query("SHOW INDEX FROM `$table` WHERE Key_name = 'billing_obj'", false);
        if ($res && db_num_rows($res) > 0)
            return;
        db_query("ALTER TABLE `$table` ADD INDEX `billing_obj` (`object_type`, `object_id`)", false);
    }


    /**
     * Per-organization data shown underneath the table and in the export:
     *  - billing_org_note   : one free-text customer note per organization
     *  - billing_org_checks : the fixed system-check list per organization,
     *                         stored as a small JSON blob (date/by/remarks per
     *                         check row). Which of the two is used is chosen by
     *                         the 'export_footer_mode' setting. Idempotent.
     */
    /**
     * osTicket resolves a thread event's name through the EVENT table
     * (ThreadEvents::log -> Event::getIdByName). Without a row there our
     * events would be stored with id 0 and never render, so seed one.
     */
    private function ensureEventType() {
        $table = defined('EVENT_TABLE') ? EVENT_TABLE : TABLE_PREFIX.'event';
        db_query("INSERT INTO `".$table."` (`name`, `description`) "
               . "SELECT ".db_input(PLUGIN_BILLING_EVENT).", ".db_input('Billing')." FROM DUAL "
               . "WHERE NOT EXISTS (SELECT 1 FROM `".$table."` WHERE `name` = ".db_input(PLUGIN_BILLING_EVENT).")", false);
    }

    private function ensureOrgTables() {
        db_query("CREATE TABLE IF NOT EXISTS `".TABLE_PREFIX."billing_org_note` (
            `org_id`     int(11) unsigned NOT NULL,
            `note`       text NOT NULL,
            `updated_by` varchar(191) NOT NULL DEFAULT '',
            `updated`    datetime NOT NULL,
            PRIMARY KEY (`org_id`)
        ) DEFAULT CHARSET=utf8;", false);
        db_query("CREATE TABLE IF NOT EXISTS `".TABLE_PREFIX."billing_org_checks` (
            `org_id`     int(11) unsigned NOT NULL,
            `data`       text NOT NULL,
            `updated_by` varchar(191) NOT NULL DEFAULT '',
            `updated`    datetime NOT NULL,
            PRIMARY KEY (`org_id`)
        ) DEFAULT CHARSET=utf8;", false);
    }

    private function ensureSchema() {
        static $checked = false;
        if ($checked)
            return;
        $checked = true;

        $table   = BILLING_TIME_TYPE_TABLE;
        $bttTbl  = TABLE_PREFIX.'billing_ticket_type';
        $res = db_query("SHOW TABLES LIKE '".$table."'", false);
        if ($res && db_num_rows($res) > 0) {
            // Table exists: make sure columns added in later versions are
            // present. Older installs created it without `factor` / `onsite`,
            // which otherwise causes "Unknown column 'factor'" on save.
            $this->ensureColumns($table);
            $this->ensureTimesheetIndex();
            $this->ensureTicketTypeColumns($bttTbl);
            $this->ensureOrgTables();
            $this->ensureEventType();
            return;
        }

        $sql = "CREATE TABLE IF NOT EXISTS `$table` (
            `id`          int(11) unsigned NOT NULL AUTO_INCREMENT,
            `name`        varchar(191) NOT NULL DEFAULT '',
            `hourly_rate` decimal(12,2) NOT NULL DEFAULT '0.00',
            `billable`    tinyint(1) unsigned NOT NULL DEFAULT '1',
            `factor`      smallint(5) unsigned NOT NULL DEFAULT '100',
            `onsite`      tinyint(1) unsigned NOT NULL DEFAULT '0',
            `travel_fee`  decimal(12,2) NOT NULL DEFAULT '0.00',
            `isdefault`   tinyint(1) unsigned NOT NULL DEFAULT '0',
            `sort`        int(11) unsigned NOT NULL DEFAULT '0',
            `isactive`    tinyint(1) unsigned NOT NULL DEFAULT '1',
            `created`     datetime NOT NULL,
            `updated`     datetime NOT NULL,
            PRIMARY KEY (`id`)
        ) DEFAULT CHARSET=utf8;";
        db_query($sql);

        // Per-ticket time type (the rate applies to the WHOLE ticket).
        // billed/billed_at: explicit "marked as billed" status, so a ticket can
        // be removed from the Open Items list without touching its time type.
        // is_goodwill: "Kulanz" - the work is recorded and reported, but
        // waived from invoicing (kept separate from the type's own factor so
        // the original rate stays visible/reportable; see computeInvoice()).
        db_query("CREATE TABLE IF NOT EXISTS `".TABLE_PREFIX."billing_ticket_type` (
            `ticket_id`    int(11) unsigned NOT NULL,
            `time_type_id` int(11) unsigned NOT NULL DEFAULT '1',
            `billed`       tinyint(1) unsigned NOT NULL DEFAULT '0',
            `billed_at`    datetime NULL DEFAULT NULL,
            `is_goodwill`  tinyint(1) unsigned NOT NULL DEFAULT '0',
            `trips`        int(10) unsigned NOT NULL DEFAULT '0',
            `updated`      datetime NOT NULL,
            PRIMARY KEY (`ticket_id`)
        ) DEFAULT CHARSET=utf8;", false);
        $this->ensureTicketTypeColumns($bttTbl);
        $this->ensureOrgTables();
        $this->ensureEventType();

        // Seed the default type (id = 1) matching Time Recording's default.
        $this->ensureColumns($table);
        $this->ensureTimesheetIndex();

        $res = db_query("SELECT COUNT(*) FROM `$table`");
        $count = ($res && ($row = db_fetch_row($res))) ? (int) $row[0] : 0;
        if ($count === 0) {
            db_query("INSERT INTO `$table` "
                ."(`id`,`name`,`hourly_rate`,`billable`,`isdefault`,`sort`,`isactive`,`created`,`updated`) "
                ."VALUES (1, 'Standard', 0.00, 1, 1, 0, 1, NOW(), NOW())");
        }
    }

    /* -- permissions ------------------------------------------------------ */

    private function canManage($staff) {
        return $staff && $staff->isAdmin();
    }

    private function canUse($staff) {
        if (!$staff) return false;
        if ($staff->isAdmin()) return true;
        $config = $this->getPluginConfig();
        return $config ? (bool) $config->get('agent_access') : false;
    }

    /* -- integration guard ------------------------------------------------ */

    /**
     * Ensure the Time Recording plugin (its ost_timesheet table) is present.
     * If not, render an explanatory notice and return false so the caller can
     * stop. $__ is the plugin-domain translator obtained via self::translate().
     */
    private function requireTimesheet($__) {
        if (Billing::timesheetAvailable())
            return true;
        $this->render('notice.tmpl.php', array(
            'message' => $__('The Time Recording plugin (table ost_timesheet) was not found. Please install and enable it first.'),
        ));
        return false;
    }

    /* -- bootstrap -------------------------------------------------------- */

    function bootstrap() {
        // SAFETY NET: a plugin must never take down the whole staff panel. If
        // wiring up throws for ANY reason, degrade gracefully (the app tile and
        // routes may be missing) instead of fataling on every request - an
        // uncaught error in bootstrap() runs on every page and shows up as a
        // login loop. The billing pages stay reachable in the normal case.
        try {
            $this->bootstrapImpl();
        } catch (\Throwable $e) {
            @error_log('[billing] bootstrap failed: '.$e->getMessage());
        }
    }

    /**
     * Declare our own thread event type. ThreadEvent::getTypedEvent() picks up
     * EVERY declared subclass by its $state, so this is the supported way to
     * add a native event - it renders in the ticket history strip exactly like
     * osTicket's own "created"/"closed" entries, with icon and timestamp.
     */
    private function declareEventClass() {
        if (!class_exists('ThreadEvent') || class_exists('BillingThreadEvent', false))
            return;
        eval('
            class BillingThreadEvent extends ThreadEvent {
                static $icon  = "dollar";
                static $state = "'.PLUGIN_BILLING_EVENT.'";
                function getDescription($mode = self::MODE_STAFF) {
                    $d = $this->getData();
                    $txt = (is_array($d) && isset($d["billing"])) ? $d["billing"] : "";
                    // use the PLUGIN catalogue, not osTicket\x27s core __(),
                    // otherwise these strings stay untranslated
                    list($__, $_N) = BillingPlugin::translate("billing");
                    $desc = $txt !== ""
                        ? sprintf($__("Billing updated by <b>{somebody}</b> {timestamp} - %s"),
                            Format::htmlchars($txt))
                        : $__("Billing updated by <b>{somebody}</b> {timestamp}");
                    return $this->template($desc, $mode);
                }
            }
        ');
    }

    function bootstrapImpl() {
        $this->declareEventClass();
        // Core helpers needed only at runtime. osTicket normally has these
        // loaded already; require them defensively (guarded) so a missing
        // optional file degrades gracefully instead of fataling.
        foreach (array('class.signal.php', 'class.app.php', 'class.dispatcher.php') as $__f) {
            if (file_exists(INCLUDE_DIR.$__f))
                require_once(INCLUDE_DIR.$__f);
        }
        if (!class_exists('Application') || !class_exists('Signal') || !function_exists('patterns'))
            return; // required framework pieces unavailable – nothing to wire up

        list($__, $_N) = self::translate('billing');
        $plugin = $this;
        // Diagnostics are opt-in via the plugin settings. While off, the
        // timing helpers return immediately and nothing is recorded.
        $diagCfg = $this->getPluginConfig();
        self::$bDiagOn = (bool) ($diagCfg && $diagCfg->get('enable_diag'));

        // 1) Add the "Billing" entry to the staff "Applications" menu.
        //
        // NOTE: bootstrap() runs before authentication, so $thisstaff is not
        // available here — a permission guard at this point would silently
        // suppress the tile for everyone. Register unconditionally (like
        // other staff apps do); access control is enforced by the page
        // handlers themselves (canUse/canManage -> 403).
        Application::registerStaffApp(
            $__('Billing'),
            ROOT_PATH.'scp/dispatcher.php/billing',
            array('title' => $__('Time & Billing'))
        );

        // Also register an admin entry. Besides being useful (direct access to
        // the time types), this works around a core bug in the apps page:
        // Application::$admin_apps is never initialised, so count($adminapps)
        // on include/staff/apps.inc.php raises a fatal error when no plugin
        // has registered an admin app.
        if (method_exists('Application', 'registerAdminApp'))
            Application::registerAdminApp(
                $__('Billing'),
                ROOT_PATH.'scp/dispatcher.php/billing/timetypes',
                array('title' => $__('Time types &amp; rates'))
            );

        // 2) Register the staff-side routes on the apps dispatcher.
        Signal::connect('apps.scp', function ($dispatcher) use ($plugin) {
            // extend() copies the individual URL matchers onto the apps
            // dispatcher (append() would wrongly add the whole sub-dispatcher).
            $dispatcher->extend(patterns('',
                url('^/billing$',                            array($plugin, 'pageOverview')),
                url('^/billing/report$',                     array($plugin, 'pageReport')),
                url('^/billing/bulk-export$',                array($plugin, 'pageBulkExport')),
            url('^/billing/upload$',                      array($plugin, 'pageUploadFile')),
            url('^/billing/timetypes$',                  array($plugin, 'pageTimeTypes')),
                url('^/billing/org(?:/(?P<id>\d+))?$',        array($plugin, 'pageOrg')),
                url('^/billing/org-note$',                    array($plugin, 'pageOrgNote')),
                url('^/billing/org-checks$',                  array($plugin, 'pageOrgChecks')),
                url('^/billing/ticket/(?P<id>\d+)$',          array($plugin, 'pageTicket')),
                url('^/billing/settype/(?P<id>\d+)$',        array($plugin, 'pageSetType')),
                url('^/billing/pending/(?P<id>\d+)$',        array($plugin, 'pageSetPending')),
                url('^/billing/dp-locale/(?P<lang>[a-zA-Z_-]+)$', array($plugin, 'pageDpLocale')),
                url('^/billing/mark-billed$',                  array($plugin, 'pageMarkBilled')),
                url('^/billing/diag$',                         array($plugin, 'pageDiag')),
                url('^/billing/task/(?P<id>\d+)$',            array($plugin, 'pageTask'))
            ));
        });

        // 3) Show a billing summary panel on the ticket detail page.
        Signal::connect('ticket.view.more', function ($ticket, &$extras) use ($plugin) {
            global $thisstaff, $ost;
            $config = $plugin->getPluginConfig();
            if (!is_object($ticket) || !$thisstaff || !$thisstaff->canAccess($ticket))
                return;
            if (!Billing::timesheetAvailable())
                return;
            list($__, $_N) = BillingPlugin::translate('billing');

            // Reconcile any pending time-type choices for this ticket (the
            // reply/note POST stored them; the timesheet row now exists).
            $plugin->reconcilePendingTypes($ticket->getId());

            // Billing summary block in the gear/"More" dropdown. This signal
            // fires inside that <ul>, so we echo an <li> with the totals and a
            // link to the invoice. The time-type selector is NOT shown here -
            // it lives below the ticket details instead, and stays available
            // even when the summary is switched off, because it is an input
            // and not just a read-out.
            if ($config && !$config->get('link_ticket_view', true)) {
                $csrf = ($ost && method_exists($ost, 'getCSRFToken')) ? $ost->getCSRFToken() : '';
                $type = (int) $plugin->getTicketType($ticket->getId());
                if ($type <= 0)
                    $type = (int) $config->get('default_time_type');
                $plugin->renderTicketTypeBox($ticket, $type, $csrf, $__);
                return;
            }

            $invoice = Billing::computeInvoice($ticket->getId(), 'T', $config);
            $mode    = ($config && $config->get('billing_mode') === 'time') ? 'time' : 'money';
            $billUrl = ROOT_PATH.'scp/dispatcher.php/billing/ticket/'.$ticket->getId();
            $sumTxt  = '<strong>'.htmlspecialchars($__('Billing'), ENT_QUOTES, 'UTF-8').':</strong> ';
            if (!empty($invoice['lines'])) {
                $sumTxt .= htmlspecialchars($__('Total time'), ENT_QUOTES, 'UTF-8').' '
                         . Billing::formatDuration($invoice['total_seconds']);
                if ($mode === 'time')
                    $sumTxt .= ' &middot; '.htmlspecialchars($__('Billable time'), ENT_QUOTES, 'UTF-8').' '
                             . Billing::formatDuration($invoice['billable_seconds'] ?? 0);
                else
                    $sumTxt .= ' &middot; '.htmlspecialchars($__('Amount'), ENT_QUOTES, 'UTF-8').' '
                             . Billing::formatMoney($invoice['total'], $config);
            } else {
                $sumTxt .= '<em>'.htmlspecialchars($__('no time recorded'), ENT_QUOTES, 'UTF-8').'</em>';
            }
            echo '<li id="billing-gear-item" style="white-space:nowrap; background:#f0f0f0;">'
               . '<span style="padding:2px 20px; display:inline-block;">'
               . $sumTxt.' &nbsp;<a href="'.$billUrl.'" class="no-pjax">['
               . htmlspecialchars($__('open invoice'), ENT_QUOTES, 'UTF-8').']</a></span></li>'
               // move our entry to the very bottom of the dropdown (below
               // "Ticket löschen"), matching the previous version's placement.
               . '<script type="text/javascript">(function(){'
               . 'function move(){var li=document.getElementById("billing-gear-item");'
               . 'if(!li||!li.parentNode)return false;'
               . 'li.parentNode.appendChild(li);return true;}'
               . 'var n=0,iv=setInterval(function(){n++;if(move()||n>40)clearInterval(iv);},200);'
               . 'if(document.readyState!=="loading"){move();}'
               . 'else{document.addEventListener("DOMContentLoaded",move);}'
               . '})();</script>';

            // Time-type selector below the whole ticket-details block.
            $csrf        = ($ost && method_exists($ost, 'getCSRFToken')) ? $ost->getCSRFToken() : '';
            $currentType = (int) $plugin->getTicketType($ticket->getId());
            if ($currentType <= 0)
                $currentType = $config ? (int) $config->get('default_time_type') : 0;
            $plugin->renderTicketTypeBox($ticket, $currentType, $csrf, $__);
        });

        // 4) Capture the chosen time type on ticket creation and on each
        //    reply/note, and apply it to the WHOLE ticket (global rate).
        $capture = function ($ticketId) use ($plugin) {
            if (!isset($_POST['billing_time_type']))
                return;
            $typeId = (int) $_POST['billing_time_type'];
            $ticketId = (int) $ticketId;
            if ($typeId <= 0)
                return;
            if ($ticketId > 0) {
                $plugin->setTicketType($ticketId, $typeId);
            } else {
                // ticket id not known yet -> remember, reconcile on next view
                if (!isset($_SESSION['billing_pending_ticket']) || !is_array($_SESSION['billing_pending_ticket']))
                    $_SESSION['billing_pending_ticket'] = array();
                $_SESSION['billing_pending_ticket'][0] = $typeId;
            }
        };
        // Pure timing markers around osTicket's own creation phases - they do
        // nothing but note how far into the request we are, so the Diagnostics
        // page shows WHERE the time goes (see mark()).
        Signal::connect('ticket.create.before', function () { BillingPlugin::mark('-- osTicket: create start'); });
        Signal::connect('ticket.create.validated', function () { BillingPlugin::mark('-- osTicket: form validated'); });
        Signal::connect('ticket.created', function ($ticket) use ($capture, $plugin) {
            if (!is_object($ticket) || !method_exists($ticket, 'getId'))
                return;
            BillingPlugin::timeStart('ticket.created');
            $ticketId = (int) $ticket->getId();
            $chosen = isset($_POST['billing_time_type']) ? (int) $_POST['billing_time_type'] : 0;
            // threadentry.created runs first and, while the thread is not yet
            // linked to the ticket, parks the choice in the session. That value
            // was written but never read - it was discarded a few lines below.
            if ($chosen <= 0 && !empty($_SESSION['billing_pending_ticket'][0]))
                $chosen = (int) $_SESSION['billing_pending_ticket'][0];
            if ($chosen > 0) {
                // an explicit choice from the create form always wins
                $plugin->setTicketType($ticketId, $chosen);
            } elseif (!$plugin->getTicketType($ticketId)) {
                // no choice AND nothing set yet -> fall back to the default.
                // (Guarded so it can never overwrite a type that was already
                //  captured, e.g. by threadentry.created during creation.)
                $cfg = $plugin->getPluginConfig();
                $def = $cfg ? (int) $cfg->get('default_time_type') : 0;
                if ($def > 0)
                    $plugin->setTicketType($ticketId, $def);
            }
            unset($_SESSION['billing_pending_ticket']);
            BillingPlugin::timeEnd('ticket.created');
        });
        Signal::connect('threadentry.created', function ($entry) use ($plugin) {
            if (!isset($_POST['billing_time_type']))
                return;
            BillingPlugin::timeStart('threadentry.created');
            $typeId = (int) $_POST['billing_time_type'];
            if ($typeId <= 0 || !is_object($entry))
                return;
            // resolve the ticket id from the entry's thread when possible
            $tid = 0;
            if (method_exists($entry, 'getThread') && ($th = $entry->getThread())
                    && method_exists($th, 'getObjectId'))
                $tid = (int) $th->getObjectId();
            if ($tid > 0)
                $plugin->setTicketType($tid, $typeId);
            else {
                if (!isset($_SESSION['billing_pending_ticket']) || !is_array($_SESSION['billing_pending_ticket']))
                    $_SESSION['billing_pending_ticket'] = array();
                $_SESSION['billing_pending_ticket'][0] = $typeId;
            }
            BillingPlugin::timeEnd('threadentry.created');
        });

        // 5) Inject the time-type picker into the NEW TICKET form too, so the
        //    rate can be chosen already at ticket creation.
        // Inject the picker into the NEW TICKET form. object.new is sent from
        // ticket-open.inc.php while the form is being rendered - the page header
        // is already out by then, so emitting here is safe. As a hard
        // safeguard we only produce output once headers have actually been
        // sent; on a successful save osTicket redirects (via Http::redirect)
        // BEFORE the form template is ever included, so this handler does not
        // run on that path and cannot break the redirect.
        Signal::connect('object.new', function ($info, $data) use ($plugin) {
            global $thisstaff;
            // Only ever emit on a GET form display. On the create/save POST the
            // ticket is created and osTicket redirects; producing ANY output
            // there (even after headers) forces the slow JS/meta-refresh
            // fallback. Restricting to GET removes that risk entirely.
            if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'GET')
                return;
            if (!$thisstaff || !Billing::timesheetAvailable())
                return;
            if (!is_array($data) || ($data['type'] ?? '') !== 'Ticket')
                return;
            list($__, $_N) = BillingPlugin::translate('billing');
            $plugin->renderTimeTypePicker(null, $__, /*createForm*/ true);
        });

    }

    /* -- ticket-global time type ------------------------------------------ */

    /** The single time type assigned to a whole ticket (or null). */
    function getTicketType($ticketId) {
        $ticketId = (int) $ticketId;
        if ($ticketId <= 0) return null;
        $res = db_query('SELECT time_type_id FROM `'.TABLE_PREFIX.'billing_ticket_type`'
                      . ' WHERE ticket_id = '.$ticketId, false);
        if ($res && ($row = db_fetch_row($res)))
            return (int) $row[0];
        return null;
    }

    /**
     * Record a billing change on the ticket itself, so the ticket history
     * shows who changed the time type / trip count and why. osTicket exposes
     * several APIs depending on the version, so we probe them in order and
     * never let a logging problem break the actual save.
     *
     * $reason is the optional free-text note entered in the dialog.
     */
    function logTicketBillingChange($ticketId, $summary, $reason = '') {
        $config = $this->getPluginConfig();
        if ($config && !$config->get('log_ticket_events', true))
            return;
        $ticketId = (int) $ticketId;
        if ($ticketId <= 0 || !class_exists('Ticket'))
            return;
        try {
            global $thisstaff;
            $ticket = Ticket::lookup($ticketId);
            if (!$ticket || !method_exists($ticket, 'logEvent'))
                return;

            // Native thread event - shows up in the ticket history strip, not
            // as a message. The optional reason is carried in the same event.
            $text = trim((string) $summary);
            $reason = trim((string) $reason);
            if ($reason !== '')
                $text .= ($text !== '' ? ' - ' : '').$reason;

            $this->declareEventClass();
            $ticket->logEvent(PLUGIN_BILLING_EVENT, array('billing' => $text), $thisstaff);
        } catch (\Throwable $e) {
            @error_log('[billing] ticket log failed: '.$e->getMessage());
        }
    }

    /** Number of billable trips ("Anfahrten") recorded for a ticket. */
    function getTicketTrips($ticketId) {
        $ticketId = (int) $ticketId;
        if ($ticketId <= 0) return 0;
        $res = db_query('SELECT trips FROM `'.TABLE_PREFIX.'billing_ticket_type`'
                      . ' WHERE ticket_id = '.$ticketId, false);
        if ($res && ($row = db_fetch_row($res)))
            return (int) $row[0];
        return 0;
    }

    /**
     * Store the number of trips for a ticket. Upserts into billing_ticket_type
     * WITHOUT touching the time type: a brand-new row takes the column default
     * time_type_id (1); an existing row keeps whatever type it already has.
     */
    function setTicketTrips($ticketId, $trips) {
        $ticketId = (int) $ticketId;
        $trips    = max(0, (int) $trips);
        if ($ticketId <= 0) return;
        $now = date('Y-m-d H:i:s');
        db_query('INSERT INTO `'.TABLE_PREFIX.'billing_ticket_type` '
               . '(ticket_id, trips, updated) VALUES ('
               . $ticketId.', '.$trips.', '.db_input($now).') '
               . 'ON DUPLICATE KEY UPDATE trips = '.$trips.', updated = '.db_input($now), false);
    }

    /**
     * Read the customer note ("Kundennotiz") for an organization.
     * Returns array('note','updated_by','updated') or null when none exists.
     */
    function getOrgNote($orgId) {
        $orgId = (int) $orgId;
        if ($orgId <= 0) return null;
        $res = db_query('SELECT note, updated_by, updated FROM `'.TABLE_PREFIX.'billing_org_note`'
                      . ' WHERE org_id = '.$orgId, false);
        if ($res && ($row = db_fetch_array($res)))
            return $row;
        return null;
    }

    /**
     * Create or overwrite the customer note for an organization. Passing an
     * empty note deletes the row (so it disappears from the exports again).
     * Returns 'saved', 'removed' or false on a bad organization id.
     */
    function setOrgNote($orgId, $note, $by = '') {
        $orgId = (int) $orgId;
        if ($orgId <= 0) return false;
        $note = (string) $note;
        // normalise line endings; keep the text otherwise verbatim
        $note = str_replace(array("\r\n", "\r"), "\n", $note);
        $now  = date('Y-m-d H:i:s');
        if (trim($note) === '') {
            db_query('DELETE FROM `'.TABLE_PREFIX.'billing_org_note` WHERE org_id = '.$orgId, false);
            return 'removed';
        }
        db_query('INSERT INTO `'.TABLE_PREFIX.'billing_org_note` '
               . '(org_id, note, updated_by, updated) VALUES ('
               . $orgId.', '.db_input($note).', '.db_input((string) $by).', '.db_input($now).') '
               . 'ON DUPLICATE KEY UPDATE note = '.db_input($note)
               . ', updated_by = '.db_input((string) $by).', updated = '.db_input($now), false);
        return 'saved';
    }

    /**
     * Built-in default table columns/rows (one per line), matching the paper
     * template. Used as the code fallback when the setting has never been
     * saved; config.php carries the editable copies shown in the settings form.
     */
    /** Built-in default table title / last-modified line (see config.php). */
    public static function defaultTableTitleText() {
        return 'Kontrolle der Kundensysteme';
    }
    public static function defaultTableMetaText() {
        return 'Zuletzt geändert am: %{date} von %{by}';
    }

    public static function defaultTableColumnsText() {
        return "Prüfungen\nDatum letzte Prüfung\n"
             . "geprüft durch Techniker / Administrator\nBemerkungen";
    }
    public static function defaultTableRowsText() {
        return "Server-Hardware\nServer-Updates\nDatensicherung lokal\n"
             . "Datensicherung extern\nSicherung TK-Anlage\n"
             . "Überprüfung NAS (Updates und Festplatten)\nAntivirenschutz";
    }

    /** Stable, readable key derived from a (possibly German) label. */
    private function slugKey($label) {
        $s = strtolower((string) $label);
        $s = strtr($s, array('ä'=>'ae','ö'=>'oe','ü'=>'ue','ß'=>'ss'));
        $t = @iconv('UTF-8', 'ASCII//TRANSLIT', $s);
        if ($t !== false) $s = $t;
        $s = preg_replace('/[^a-z0-9]+/', '_', strtolower($s));
        $s = trim((string) $s, '_');
        return $s === '' ? 'row' : substr($s, 0, 40);
    }

    /** Split a textarea setting into trimmed, non-empty lines. */
    private function configLines($key, $fallback) {
        $config = $this->getPluginConfig();
        $raw = $config ? (string) $config->get($key, $fallback) : $fallback;
        $out = array();
        foreach (preg_split('/\r\n|\r|\n/', $raw) as $line) {
            $line = trim($line);
            if ($line !== '') $out[] = $line;
        }
        return $out;
    }

    /** Turn labels into ordered [ ['key'=>slug,'label'=>text], ... ] with unique keys. */
    private function keyedDefs(array $labels) {
        $defs = array(); $seen = array();
        foreach ($labels as $label) {
            $key = $this->slugKey($label);
            $base = $key; $i = 2;
            while (isset($seen[$key])) { $key = $base.'_'.$i; $i++; }
            $seen[$key] = true;
            $defs[] = array('key' => $key, 'label' => $label);
        }
        return $defs;
    }

    /** Configured table title (may be empty). */
    function tableTitle() {
        $config = $this->getPluginConfig();
        // Fall back to the built-in default only when the setting has never
        // been saved; an explicitly emptied field stays empty (= no title).
        return $config ? (string) $config->get('table_title', self::defaultTableTitleText())
                       : self::defaultTableTitleText();
    }

    /** Configured template for the bold "last modified" line (may be empty). */
    function tableMetaText() {
        $config = $this->getPluginConfig();
        return $config ? (string) $config->get('table_meta_text', self::defaultTableMetaText())
                       : self::defaultTableMetaText();
    }

    /**
     * Resolve the "last modified" line for one organization. Returns '' when
     * the template is empty or nothing has been saved yet (no timestamp).
     * The text is run through the plugin's own placeholder engine, so the same
     * {x} / %{x} / %{report.x} syntax as every other text field works here.
     * $reportVals (optional) supplies the standard report tokens (org, from,
     * to, count, billable, total, …); this line additionally provides %{date}
     * (the change timestamp) and %{by} (the employee), which take precedence.
     */
    function tableMetaLine($updated, $updatedBy, $reportVals = array()) {
        $tpl = $this->tableMetaText();
        if (trim($tpl) === '' || trim((string) $updated) === '')
            return '';
        $vals = array_merge(is_array($reportVals) ? $reportVals : array(), array(
            'date' => Billing::formatDateTime($updated),   // localized, overrides the report date
            'by'   => (string) $updatedBy,
        ));
        return Billing::applyTokens($tpl, $vals);
    }

    /**
     * Build the export file name from the configurable pattern, using the
     * plugin's native placeholder system (%{report.org}, %{report.year},
     * %{report.month}, ...). The month/year refer to the END of
     * the reported period (that is the month the document belongs to), falling
     * back to today when no period is given. Anything unsafe for a file name
     * is replaced, so the result is always a valid download name.
     */
    private function exportFileName($filters, $ext) {
        $config  = $this->getPluginConfig();
        $pattern = $config ? trim((string) $config->get('export_filename', '')) : '';
        if ($pattern === '')
            $pattern = self::defaultExportFileName();

        // Month/year refer to the END of the reported period - that is the
        // month the document belongs to - falling back to today.
        $to   = isset($filters['date_to'])   ? (string) $filters['date_to']   : '';
        $from = isset($filters['date_from']) ? (string) $filters['date_from'] : '';
        $ts   = $to !== '' ? strtotime($to) : time();
        if (!$ts) $ts = time();

        // Same placeholder engine as every other text field in the plugin.
        $vals = Billing::reportValues(array(), $config, array(
            'org'   => isset($filters['org_name']) && $filters['org_name'] !== ''
                         ? $filters['org_name'] : $this->translateOrgFallback(),
            'from'  => $from,          // formatted centrally in reportValues()
            'to'    => $to,
            'date'  => date('Y-m-d', $ts),
            'year'  => date('Y', $ts),
            'month' => date('m', $ts),
            'day'   => date('d', $ts),
        ));
        $name = Billing::applyTokens($pattern, $vals);

        // make it filesystem/header safe
        $name = str_replace(array('/', '\\', ':', '*', '?', '"', '<', '>', '|', "\n", "\r"), '-', $name);
        $name = strtr($name, array('ä'=>'ae','ö'=>'oe','ü'=>'ue','Ä'=>'Ae','Ö'=>'Oe','Ü'=>'Ue','ß'=>'ss'));
        $name = preg_replace('/\s+/', '_', trim($name));
        $name = preg_replace('/_{2,}/', '_', $name);
        $name = trim((string) $name, '_-.');
        if ($name === '') $name = 'billing-report';
        return $name.'.'.$ext;
    }

    /** Built-in default for the export file name (native placeholders). */
    public static function defaultExportFileName() {
        return '%{report.org}_Leistungsnachweis_%{report.year}-%{report.month}';
    }

    /** Label used for %{report.org} when the report covers all organizations. */
    private function translateOrgFallback() {
        list($__, $_N) = self::translate('billing');
        return $__('All organizations');
    }

    /** Standard report token values (org/from/to + totals) for a table/export. */
    private function reportTokenVals($rows, $filters, $config) {
        return Billing::reportValues($rows, $config, array(
            'org'  => isset($filters['org_name'])  ? $filters['org_name']  : '',
            'from' => isset($filters['date_from']) ? $filters['date_from'] : '',
            'to'   => isset($filters['date_to'])   ? $filters['date_to']   : '',
        ));
    }

    /**
     * Everything the shared "section below the table" editor
     * (_footer_editor.tmpl.php) needs for one organization. Used by both the
     * organization page and the general report page so the on-screen editor
     * matches exactly what the export prints.
     */
    private function footerVars($org, $rows, $config, $metaFilters) {
        $ot = $org ? $this->getOrgTable($org->getId())
                   : array('rows'=>array(),'updated'=>'','updated_by'=>'');
        $metaVals = $this->reportTokenVals($rows, $metaFilters, $config);
        return array(
            'footerMode'    => $config ? $config->get('export_footer_mode', 'note') : 'note',
            'orgNote'       => $org ? $this->getOrgNote($org->getId()) : null,
            'noteDefault'   => $config ? (string) $config->get('note_default_text', '') : '',
            'orgTable'      => $ot,
            'tableTitle'    => $this->tableTitle(),
            'tableMetaLine' => $this->tableMetaLine($ot['updated'], $ot['updated_by'], $metaVals),
            'tableCols'     => $this->tableColumnDefs(),
            'tableRows'     => $this->tableRowDefs(),
        );
    }

    /**
     * Column definitions. The first configured line is the heading of the
     * left (row-name) column; every further line is an editable data column.
     * Returns array('name' => 'Heading', 'data' => [ ['key','label'], ... ]).
     */
    function tableColumnDefs() {
        $lines = $this->configLines('table_columns', self::defaultTableColumnsText());
        $name  = count($lines) ? array_shift($lines) : '';
        return array('name' => $name, 'data' => $this->keyedDefs($lines));
    }

    /** Row definitions: ordered [ ['key'=>slug,'label'=>text], ... ]. Empty => no rows. */
    function tableRowDefs() {
        return $this->keyedDefs($this->configLines('table_rows', self::defaultTableRowsText()));
    }

    /**
     * Read the per-organization table data. Returns
     *   array('rows' => map(rowKey => array('active'=>bool,'cells'=>map)),
     *         'updated' => datetime|'', 'updated_by' => string).
     */
    function getOrgTable($orgId) {
        $orgId = (int) $orgId;
        $empty = array('rows' => array(), 'updated' => '', 'updated_by' => '');
        if ($orgId <= 0) return $empty;
        $res = db_query('SELECT data, updated_by, updated FROM `'.TABLE_PREFIX.'billing_org_checks`'
                      . ' WHERE org_id = '.$orgId, false);
        if ($res && ($row = db_fetch_array($res))) {
            $d = json_decode((string) $row['data'], true);
            return array(
                'rows'       => is_array($d) ? $d : array(),
                'updated'    => (string) $row['updated'],
                'updated_by' => (string) $row['updated_by'],
            );
        }
        return $empty;
    }

    /**
     * Create or overwrite an organization's table data. Only known row/column
     * keys are kept. A row is stored when it is switched off OR carries any
     * content (so a disabled empty row is remembered). When nothing needs
     * storing the row is deleted. Returns 'saved', 'removed' or false.
     */
    function setOrgTable($orgId, $data, $by = '') {
        $orgId = (int) $orgId;
        if ($orgId <= 0) return false;
        if (!is_array($data)) $data = array();
        $cols = $this->tableColumnDefs();
        $colKeys = array();
        foreach ($cols['data'] as $c) $colKeys[] = $c['key'];
        $clean = array();
        foreach ($this->tableRowDefs() as $rdef) {
            $rk = $rdef['key'];
            $in = (isset($data[$rk]) && is_array($data[$rk])) ? $data[$rk] : null;
            if ($in === null) {
                // row was not part of the submitted form at all
                $active = true;
                $inCells = array();
            } else {
                // the row was rendered: the "active" checkbox is only present
                // in the POST when it is ticked, so its presence IS the state.
                $active  = isset($in['active']);
                $inCells = (isset($in['cells']) && is_array($in['cells'])) ? $in['cells'] : array();
            }
            $cells = array();
            $hasContent = false;
            foreach ($colKeys as $ck) {
                $v = isset($inCells[$ck]) ? trim(substr((string) $inCells[$ck], 0, 2000)) : '';
                if ($v !== '') { $cells[$ck] = $v; $hasContent = true; }
            }
            if ($hasContent || !$active)
                $clean[$rk] = array('active' => $active, 'cells' => $cells);
        }
        $now = date('Y-m-d H:i:s');
        if (!$clean) {
            db_query('DELETE FROM `'.TABLE_PREFIX.'billing_org_checks` WHERE org_id = '.$orgId, false);
            return 'removed';
        }
        $json = json_encode($clean, JSON_UNESCAPED_UNICODE);
        db_query('INSERT INTO `'.TABLE_PREFIX.'billing_org_checks` '
               . '(org_id, data, updated_by, updated) VALUES ('
               . $orgId.', '.db_input($json).', '.db_input((string) $by).', '.db_input($now).') '
               . 'ON DUPLICATE KEY UPDATE data = '.db_input($json)
               . ', updated_by = '.db_input((string) $by).', updated = '.db_input($now), false);
        return 'saved';
    }

    /**
     * Explicit "billed" status for a ticket. Marking a ticket as billed
     * removes it from the Open Items list without touching its time type or
     * recorded time - it is a pure bookkeeping flag ("we have invoiced this").
     */
    function isBilled($ticketId) {
        $ticketId = (int) $ticketId;
        if ($ticketId <= 0) return false;
        $res = db_query('SELECT billed FROM `'.TABLE_PREFIX.'billing_ticket_type`'
                      . ' WHERE ticket_id = '.$ticketId, false);
        if ($res && ($row = db_fetch_row($res)))
            return ((int) $row[0]) === 1;
        return false;
    }

    /** Mark one or more tickets as billed (or open again) in one go. */
    function setBilled($ticketIds, $billed) {
        $ids = array_filter(array_map('intval', (array) $ticketIds), function ($v) { return $v > 0; });
        if (!$ids) return 0;
        $now     = date('Y-m-d H:i:s');
        $flag    = $billed ? 1 : 0;
        $billedAt = $billed ? db_input($now) : 'NULL';
        $n = 0;
        foreach ($ids as $tid) {
            db_query('INSERT INTO `'.TABLE_PREFIX.'billing_ticket_type` '
                   . '(ticket_id, time_type_id, billed, billed_at, updated) '
                   . 'VALUES ('.$tid.', 1, '.$flag.', '.$billedAt.', '.db_input($now).') '
                   . 'ON DUPLICATE KEY UPDATE billed = '.$flag.', billed_at = '.$billedAt.', updated = '.db_input($now), false);
            $n++;
        }
        return $n;
    }

    /**
     * "Kulanz" (goodwill): the ticket's time is recorded and reported as
     * usual, but waived from invoicing. Kept as its own flag rather than a
     * factor of 0 on the time type, so the type/rate stays intact and visible
     * for reporting - only the invoiced amount/billable time is zeroed. See
     * Billing::computeInvoice() for where this is applied.
     */
    function isGoodwill($ticketId) {
        $ticketId = (int) $ticketId;
        if ($ticketId <= 0) return false;
        $res = db_query('SELECT is_goodwill FROM `'.TABLE_PREFIX.'billing_ticket_type`'
                      . ' WHERE ticket_id = '.$ticketId, false);
        if ($res && ($row = db_fetch_row($res)))
            return ((int) $row[0]) === 1;
        return false;
    }

    function setGoodwill($ticketId, $goodwill) {
        $ticketId = (int) $ticketId;
        if ($ticketId <= 0) return;
        $now  = date('Y-m-d H:i:s');
        $flag = $goodwill ? 1 : 0;
        db_query('INSERT INTO `'.TABLE_PREFIX.'billing_ticket_type` '
               . '(ticket_id, time_type_id, is_goodwill, updated) VALUES ('.$ticketId.', 1, '.$flag.', '.db_input($now).') '
               . 'ON DUPLICATE KEY UPDATE is_goodwill = '.$flag.', updated = '.db_input($now), false);
    }

    /**
     * Assign a time type to the WHOLE ticket: remember it and apply it to
     * every timesheet row of that ticket (the rate is global per ticket).
     */
    function setTicketType($ticketId, $typeId) {
        $ticketId = (int) $ticketId; $typeId = (int) $typeId;
        if ($ticketId <= 0 || $typeId <= 0)
            return;
        // During a single ticket save this is reached more than once
        // (threadentry.created for the first message AND ticket.created, plus
        // any auto-response entries). Each call used to run an INSERT and a
        // full UPDATE over the timesheet - remember what we already applied
        // and skip the repeats.
        static $applied = array();
        if (isset($applied[$ticketId]) && $applied[$ticketId] === $typeId)
            return;
        $applied[$ticketId] = $typeId;
        $now = date('Y-m-d H:i:s');
        db_query('INSERT INTO `'.TABLE_PREFIX.'billing_ticket_type` '
               . '(ticket_id, time_type_id, updated) VALUES ('.$ticketId.', '.$typeId.', '.db_input($now).') '
               . 'ON DUPLICATE KEY UPDATE time_type_id = '.$typeId.', updated = '.db_input($now), false);
        if (Billing::timesheetAvailable())
            db_query('UPDATE `'.BILLING_TIMESHEET_TABLE.'` SET time_type_id = '.$typeId
                   . " WHERE object_type = 'T' AND object_id = ".$ticketId
                   . ' AND time_type_id <> '.$typeId, false);
    }

    /**
     * Reconcile a ticket: if it has an assigned type, make sure every
     * timesheet row (including newly booked ones) uses it. Also applies a
     * pending choice captured on the create/reply POST.
     */
    function reconcilePendingTypes($ticketId) {
        $ticketId = (int) $ticketId;
        // pending choice from the last POST (create or reply)
        if (!empty($_SESSION['billing_pending_ticket']) && is_array($_SESSION['billing_pending_ticket'])) {
            foreach ($_SESSION['billing_pending_ticket'] as $tid => $typeId) {
                $this->setTicketType((int) $tid, (int) $typeId);
                unset($_SESSION['billing_pending_ticket'][$tid]);
            }
        }
        // enforce the stored ticket type on all (incl. new) rows
        $stored = $this->getTicketType($ticketId);
        if ($stored && Billing::timesheetAvailable())
            db_query('UPDATE `'.BILLING_TIMESHEET_TABLE.'` SET time_type_id = '.$stored
                   . " WHERE object_type = 'T' AND object_id = ".$ticketId
                   . ' AND time_type_id <> '.$stored, false);
    }

    /** Emit a script that inserts a time-type <select> into the ticket forms. */
    function renderTicketTypeBox($ticket, $currentType, $csrf, $__) {
        $types = BillingTimeType::getActiveList();
        if (!$types)
            return;
        $dlgUrl  = ROOT_PATH.'scp/dispatcher.php/billing/settype/'.$ticket->getId();
        // current value label shown in the detail row
        $curLabel = $__('None');
        if (isset($types[$currentType])) {
            $curLabel = $types[$currentType]->getName();
            $f = (int) $types[$currentType]->getFactor();
            if ($f !== 100) $curLabel .= ' ('.$f.'%)';
        }
        $trips = (int) $this->getTicketTrips($ticket->getId());
        if ($trips > 0)
            $curLabel .= '  -  '.sprintf($__('Trips: %d'), $trips);
        $labelTxt = htmlspecialchars($__('Billing time type'), ENT_QUOTES, 'UTF-8');
        $curSafe  = htmlspecialchars($curLabel, ENT_QUOTES, 'UTF-8');
        $updTxt   = htmlspecialchars($__('Update'), ENT_QUOTES, 'UTF-8');
        // Native detail row (same injection pattern as the Time Recording
        // plugin) with an inline-edit link that opens osTicket's popup dialog.
        echo '<script type="text/javascript">'
           . '$(function(){'
           . '  if ($("#billing-tt-row").length === 0) {'
           . '    var headerForm = $("table.ticket_info table").first();'
           . '    var content = \'<tr id="billing-tt-row"><th>'.$labelTxt.':<\\/th>\''
           . '                + \'<td><a class="inline-edit-billing" href="#" \''
           . '                + \'data-placement="bottom" data-toggle="tooltip" title="'.$updTxt.'">\''
           . '                + \'<span id="field_billing_tt">'.addslashes($curSafe).'<\\/span><\\/a><\\/td><\\/tr>\';'
           . '    headerForm.append(content);'
           . '  }'
           . '  $(document).off("click.billingtt").on("click.billingtt", "a.inline-edit-billing", function(e){'
           . '    e.preventDefault();'
           . '    $.dialog("'.$dlgUrl.'?_uid="+new Date().getTime(), [201], function(xhr){'
           . '      try { var o=$.parseJSON(xhr.responseText);'
           . '        if(o && o.id && o.value){ $("#field_"+o.id).html(o.value); } } catch(e){}'
           . '    });'
           . '  });'
           . '});'
           . '</script>';
    }

    function renderTimeTypePicker($ticket, $__, $createForm = false) {
        $types = BillingTimeType::getActiveList();
        if (!$types)
            return;
        $current = is_object($ticket) && method_exists($ticket, 'getId')
            ? (int) $this->getTicketType($ticket->getId()) : 0;
        if ($current <= 0) {
            // fall back to the configured default so it is pre-selected
            $cfg = $this->getPluginConfig();
            $current = $cfg ? (int) $cfg->get('default_time_type') : 0;
        }
        $opts = '';
        foreach ($types as $tid => $tt) {
            $label = $tt->getName();
            $fac   = (int) $tt->getFactor();
            if ($fac !== 100) $label .= ' ('.$fac.'%)';
            $sel = ($current === (int) $tid) ? ' selected' : '';
            $opts .= '<option value="'.(int) $tid.'"'.$sel.'>'
                   . htmlspecialchars($label, ENT_QUOTES, 'UTF-8').'</option>';
        }
        $labelTxt = htmlspecialchars($__('Billing time type'), ENT_QUOTES, 'UTF-8');
        // Match the Time Recording timer row structure (a <tr> in the form's
        // table) so the picker sits directly under "Bearbeitungszeit".
        $rowHtml = '<tr class="billing-tt-row">'
                 . '<td style="width:130px;"><strong>'.$labelTxt.':</strong></td>'
                 . '<td><select name="billing_time_type" class="billing-tt-select nowarn">'.$opts.'</select></td>'
                 . '</tr>';
        $json = json_encode($rowHtml);
        // The Time Recording plugin injects its timer row asynchronously and
        // marks it with the ptStartStopButton / processingTime fields. We poll
        // for that row and drop our own row right after it, in the same table.
        echo '<script type="text/javascript">(function(){'
           . 'function timerRow(){'
           . '  var el=document.querySelector(".ptStartStopButton, input[name=processingTime], .ptTimerDisplay, .ptInputFieldTime");'
           . '  return el ? (el.closest("tr")) : null;'
           . '}'
           // Locate osTicket's actual ticket-create <form>. Using the field\'s
           // own .form pointer is reliable even when the picker/timer sits in a
           // different table than the form fields.
           . 'function findTicketForm(){'
           . '  var p=document.querySelector("input[name=a][value=open]");'
           . '  if(p && p.form) return p.form;'
           . '  p=document.querySelector("[name=subject], textarea[name=message]");'
           . '  if(p){ var f=p.form || (p.closest ? p.closest("form") : null); if(f) return f; }'
           . '  p=document.querySelector("input[name=processingTime]");'
           . '  if(p && p.form) return p.form;'
           . '  var fs=document.querySelectorAll("form");'
           . '  for(var i=0;i<fs.length;i++){'
           . '    var m=(fs[i].getAttribute("method")||"").toLowerCase();'
           . '    if(m==="post" && fs[i].querySelector("input[type=submit], button[type=submit], button:not([type])")) return fs[i];'
           . '  }'
           . '  return null;'
           . '}'
           . 'function inject(){'
           . '  if(document.querySelector(".billing-tt-row")) return true;'
           . '  var tr=timerRow(); if(!tr || !tr.parentNode) return false;'
           . '  var tmp=document.createElement("tbody"); tmp.innerHTML='.$json.';'
           . '  var row=tmp.firstChild;'
           . '  tr.parentNode.insertBefore(row, tr.nextSibling);'
           // The row keeps its original spot below the timer. To be submitted
           // it needs to live in the same form context as the Time Recording
           // plugin, so mirror the value into a hidden input placed right next
           // to THEIR hidden field - that one demonstrably reaches the server.
           . '  var sel=row.querySelector("select");'
           . '  var host=document.querySelector("input[name=processingTime].ptHiddenFieldValue")'
           . '        || document.querySelector("input[name=processingTime]");'
           . '  if(sel && host && host.parentNode && !document.querySelector(".billing-tt-value")){'
           . '    var hid=document.createElement("input");'
           . '    hid.type="hidden"; hid.name="billing_time_type";'
           . '    hid.className="billing-tt-value"; hid.value=sel.value;'
           . '    host.parentNode.appendChild(hid);'
           . '    sel.removeAttribute("name");'
           . '    sel.addEventListener("change", function(){ hid.value = sel.value; });'
           . '  }'
           // PRIMARY, submit-mechanism-independent delivery: keep a hidden
           // billing_time_type field INSIDE osTicket\'s own ticket form, synced
           // to the picker. A field that lives in the submitted form is posted
           // no matter HOW the form is sent - a real click, the Enter key, or a
           // programmatic form.submit() (which timer plugins use on submit and
           // which fires NO "submit" event, so the capture handler below never
           // runs). That missing event is why the picker choice was still lost.
           . '  if(sel){'
           . '    var tf=findTicketForm();'
           . '    if(tf){'
           . '      var fh=tf.querySelector("input[name=billing_time_type]");'
           . '      if(!fh){'
           . '        fh=document.createElement("input");'
           . '        fh.type="hidden"; fh.name="billing_time_type"; fh.className="billing-tt-form";'
           . '        tf.appendChild(fh);'
           . '      }'
           . '      fh.value=sel.value;'
           . '      sel.addEventListener("change", function(){ fh.value=sel.value; });'
           . '    }'
           . '  }'
           // Report the choice to the server as well. This does not depend on
           // where the field sits in the DOM, so the value survives even when
           // the picker ends up outside the submitted form.
           . '  if(sel){'
           . '    var report=function(){'
           // GET on purpose: osTicket enforces its CSRF check only for
           // POST/PUT/PATCH/DELETE, and a POST without a matching token is
           // answered with "400 Valid CSRF Token Required". This call only
           // stores the selection in the user's own session.
           . '      try { var x=new XMLHttpRequest();'
           . '        x.open("GET", "'.ROOT_PATH.'scp/dispatcher.php/billing/pending/"+encodeURIComponent(sel.value), true);'
           . '        x.send();'
           . '      } catch(e){}'
           . '    };'
           . '    sel.addEventListener("change", report);'
           . '  }'
           . '  return true;'
           . '}'
           // Safety net that makes the choice reach the server reliably.
           // The two channels above are fragile: the hidden mirror is only
           // submitted when it happens to sit inside the posted <form>, and the
           // "pending" XHR is fired async on change - a create-form submit
           // navigates the page away and the browser ABORTS that in-flight
           // request, so it never lands. When both miss, ticket.created sees no
           // choice and applies the default, silently discarding the selection.
           // A capture-phase submit handler removes that dependency entirely:
           // right before the form is serialised (synchronously, before any
           // navigation) it writes/updates a hidden billing_time_type field
           // INSIDE the exact form being submitted, from the picker's current
           // value. No DOM position or async timing can lose the value now.
           . 'if(!window.__billingTtSubmitHook){'
           . '  window.__billingTtSubmitHook=true;'
           . '  document.addEventListener("submit", function(ev){'
           . '    var form=ev.target;'
           . '    if(!form || form.tagName!=="FORM") return;'
           . '    if((form.getAttribute("method")||"").toLowerCase()==="get") return;'
           . '    var s=document.querySelector(".billing-tt-select");'
           . '    if(!s) return;'
           . '    var hid=form.querySelector("input[name=billing_time_type]");'
           . '    if(!hid){'
           . '      hid=document.createElement("input");'
           . '      hid.type="hidden"; hid.name="billing_time_type";'
           . '      hid.className="billing-tt-submit";'
           . '      form.appendChild(hid);'
           . '    }'
           . '    hid.value=s.value;'
           . '  }, true);'
           . '}'
           // retry: the timer is added on DOM-ready by the other plugin, and
           // again after tab switches; poll briefly until both exist.
           . 'var tries=0;'
           . 'var iv=setInterval(function(){ tries++; if(inject() || tries>40) clearInterval(iv); }, 250);'
           . 'if(document.readyState!=="loading"){ inject(); }'
           . 'else{ document.addEventListener("DOMContentLoaded", inject); }'
           . '})();</script>';
    }

    /* -- helpers for the page handlers ------------------------------------ */

    /**
     * osTicket's header/footer templates are written for plain-file scope and
     * dereference several globals directly ($cfg->getAllowIframes(), $ost->
     * getPageTitle(), ...). When including them inside a method, those
     * globals must be imported explicitly or the templates fatal on null.
     */
    private function header() {
        global $ost, $cfg, $thisstaff, $nav, $errors, $msg;
        if (!isset($errors)) $errors = array();
        if ($nav) $nav->setActiveTab('apps');
        require_once(STAFFINC_DIR.'header.inc.php');

        // osTicket's ".dp" datepicker appends a separate trigger button
        //   <input class="dp"><button class="ui-datepicker-trigger"><img src="./images/cal.png"></button>
        // whose image path is relative and therefore broken on our pages, and
        // whose box never lines up with the input. Instead of fighting that
        // button, we drop it and paint the calendar INSIDE the input as a
        // background image: being part of the field, it is vertically centred
        // by definition. Clicking the field still opens the picker because
        // osTicket initialises it with showOn:'both'.
        $svg = '%3Csvg%20xmlns%3D%27http%3A//www.w3.org/2000/svg%27%20viewBox%3D%270%200%2016%2016%27'
             . '%20fill%3D%27none%27%20stroke%3D%27%23777%27%20stroke-width%3D%271.2%27%3E'
             . '%3Crect%20x%3D%271.5%27%20y%3D%272.5%27%20width%3D%2713%27%20height%3D%2712%27%20rx%3D%271.5%27/%3E'
             . '%3Cline%20x1%3D%271.5%27%20y1%3D%276.5%27%20x2%3D%2714.5%27%20y2%3D%276.5%27/%3E'
             . '%3Cline%20x1%3D%275%27%20y1%3D%271%27%20x2%3D%275%27%20y2%3D%274%27/%3E'
             . '%3Cline%20x1%3D%2711%27%20y1%3D%271%27%20x2%3D%2711%27%20y2%3D%274%27/%3E%3C/svg%3E';
        echo '<style type="text/css">'
           // no separate trigger button at all
           . '.ui-datepicker-trigger{display:none!important;}'
           // calendar icon inside the field, vertically centred by the browser
           . 'input.dp{'
           . 'background-image:url("data:image/svg+xml;charset=utf-8,'.$svg.'")!important;'
           . 'background-repeat:no-repeat!important;'
           . 'background-position:right 7px center!important;'
           . 'background-size:15px 15px!important;'
           . 'padding-right:28px!important;'
           . 'cursor:pointer;'
           . '}'
           // Filter bars: flexbox centres every control on one line regardless
           // of font metrics (labels, selects, date inputs, buttons).
           . '.billing-filters{display:flex!important;align-items:center!important;'
           . 'flex-wrap:wrap;gap:6px;}'
           . '.billing-filters>*{align-self:center;}'
           . '.billing-filters label{margin:0!important;}'
           . '.billing-filters input,.billing-filters select{margin:0!important;}'
           // Headings that carry the column-editor gear: same idea.
           // Heading + column-editor gear: stretch the gear container over the
           // full heading height and centre the icon inside it. Same trick as
           // the date fields - no dependency on font metrics.
           . '.billing-head{display:flex!important;align-items:stretch!important;gap:8px;}'
           . '.billing-coledit{display:flex!important;align-items:center!important;}'
           . '.billing-coledit>summary{display:flex!important;align-items:center!important;}'
           // light grey panel used for all filter/tool bars
           . '.billing-panel{background:#f4f4f4;border:1px solid #ddd;padding:8px;}'
           . '</style>';

        // Native-style row selection for the billing tables: "Select
        // All / None / Toggle" links, matching osTicket's own ticket queues
        // (no select-all checkbox in the header row).
        echo '<script type="text/javascript">'
           . 'function billingSel(el, mode){'
           . '  var f = el; while (f && f.tagName !== "FORM") f = f.parentNode;'
           . '  if (!f) return;'
           . '  var boxes = f.querySelectorAll(\'input[name="ticket_ids[]"]\');'
           . '  for (var i=0;i<boxes.length;i++){'
           . '    boxes[i].checked = (mode === -1) ? !boxes[i].checked : (mode === 1);'
           . '  }'
           . '}'
           . '</script>';

        // Auto-load the jQuery UI datepicker's calendar-language file that
        // matches the current interface language (osTicket ships them but
        // never loads them, so the calendar always shows English month/day
        // names regardless of the staff's language). The numeric date FORMAT
        // itself is left untouched: osTicket already drives it dynamically
        // from the system's date-format setting (c.date_format) for every
        // ".dp" field, ours included, so only the calendar's language needs
        // fixing here.
        $locUrl = ROOT_PATH.'scp/dispatcher.php/billing/dp-locale/';
        echo '<script type="text/javascript">(function(){'
           // A quick-select period (e.g. "Current month") OVERRIDES the two
           // date fields server-side. So as soon as the user edits a date,
           // switch the period select to "Custom" - otherwise the preset would
           // silently replace the typed dates and the filter appears to reset.
           . 'function bindDates(){'
           . '  if(!window.jQuery) return;'
           . '  jQuery(document).off("change.billingdate").on("change.billingdate", "input.billing-date", function(){'
           . '    var f = jQuery(this).closest("form");'
           . '    f.find("select[name=\'range\']").val("");'
           . '  });'
           . '}'
           . 'if(window.jQuery){ jQuery(bindDates); } else { setTimeout(bindDates, 800); }'
           . '})();</script>';

        echo '<script type="text/javascript">(function(){'
           . 'var htmlLang = (document.documentElement.lang || "").toLowerCase();'
           . 'var code = (htmlLang.split(/[-_]/)[0] || "").replace(/[^a-z]/g,"");'
           . 'if (!code || code === "en") return;'
           . 'var loaded = false, loading = false;'
           . 'function localizeAll(){'
           . '  var reg = jQuery.datepicker.regional[code];'
           . '  if (!reg) return;'
           // Only take the LANGUAGE strings. The locale file also carries its
           // own dateFormat and calls setDefaults(), which would leave the
           // picker parsing a different format than the value we render into
           // the field - the picker then fails to parse it and CLEARS the
           // field, breaking the date filter. osTicket'\''s own date format
           // stays authoritative.
           . '  var lang = {}; var keep = ["closeText","prevText","nextText","currentText",'
           . '    "monthNames","monthNamesShort","dayNames","dayNamesShort","dayNamesMin",'
           . '    "weekHeader","firstDay","isRTL","showMonthAfterYear","yearSuffix"];'
           . '  for (var i=0;i<keep.length;i++) if (reg[keep[i]] !== undefined) lang[keep[i]] = reg[keep[i]];'
           . '  jQuery(".dp").each(function(){'
           . '    var $f = jQuery(this);'
           . '    if (!$f.data("datepicker") || $f.data("billingDpLocalized")) return;'
           // Re-applying datepicker options makes it re-read the input, which
           // can blank a value the user already picked. Snapshot the raw value
           // and put it back if that happens.
           . '    var keepVal = $f.val();'
           . '    $f.datepicker("option", lang);'
           . '    if (keepVal && $f.val() !== keepVal) $f.val(keepVal);'
           . '    $f.data("billingDpLocalized", true);'
           . '  });'
           . '}'
           . 'function tick(){'
           . '  if (!window.jQuery || !jQuery.datepicker) return;'
           . '  if (!loading && !loaded) {'
           . '    loading = true;'
           // remember the global date format before the locale file runs:
           // it calls setDefaults() with its own format, which would apply to
           // any picker initialised later and break value parsing.
           . '    var origFmt = jQuery.datepicker._defaults ? jQuery.datepicker._defaults.dateFormat : null;'
           . '    jQuery.getScript("'.$locUrl.'" + code).done(function(){ loaded = true;'
           . '      if (origFmt) jQuery.datepicker.setDefaults({dateFormat: origFmt});'
           . '      localizeAll(); })'
           . '      .fail(function(){ loaded = true; }); '
           . '  }'
           . '  if (loaded) localizeAll();'
           . '}'
           . 'var n=0, iv=setInterval(function(){ n++; tick(); if(n>50) clearInterval(iv); }, 200);'
           . '})();</script>';
    }

    private function footer() {
        global $ost, $cfg, $thisstaff, $nav;
        require_once(STAFFINC_DIR.'footer.inc.php');
    }

    private function render($template, array $vars = array()) {
        global $thisstaff, $cfg, $nav, $ost, $msg, $errors;
        list($__, $_N) = self::translate('billing');
        $config = $this->getPluginConfig();
        extract($vars);
        // Route our own notices/errors through osTicket's native banners
        // (green "#msg_notice" / red "#msg_error" with icon), rendered by
        // header.inc.php, so they match every other page in the helpdesk.
        // Route our notices through osTicket's native Messages API so they
        // render as the standard green ".success-banner" / red error banner
        // used everywhere else in the helpdesk. Fall back to the classic
        // $msg / $errors globals if the API is unavailable.
        // Emit notices/errors in osTicket's exact native markup so they look
        // identical to every other page (green "#msg_notice" / red
        // "#msg_error"). header() opens #content via header.inc.php; these
        // divs then sit at the top of it, exactly where the core places them.
        $this->header();
        if (!empty($vars['error']))
            echo '<div id="msg_error">'.Format::htmlchars($vars['error']).'</div>';
        elseif (!empty($vars['notice']))
            echo '<div id="msg_notice">'.Format::htmlchars($vars['notice']).'</div>';
        include dirname(__file__).'/templates/'.$template;
        $this->footer();
    }

    /* -- page handlers (called by the dispatcher) ------------------------- */

    function pageOverview() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));

        if (!$this->requireTimesheet($__))
            return;

        // Ticket-number search: resolve and redirect to the ticket page.
        $searchError = null;
        if (!empty($_GET['ticketno'])) {
            $num = trim($_GET['ticketno']);
            if (($tid = Ticket::getIdByNumber($num)))
                Http::redirect(ROOT_PATH.'scp/dispatcher.php/billing/ticket/'.((int) $tid));
            $searchError = sprintf($__('No ticket found for number "%s".'), Format::htmlchars($num));
        }

        $config    = $this->getPluginConfig();
        $catalog   = $this->reportCatalog($__);
        $notice    = null;
        $this->handleSaveColumns($catalog, $__, $notice);
        // open items aggregate per object: no per-entry columns here
        $columns     = $this->loadColumns($catalog);
        $fullColumns = $columns;
        $openItems   = Billing::getOpenItems($config);
        // normalise to the shared row shape used by exportCell()
        $rows = array();
        foreach ($openItems as $it) {
            $rows[] = array(
                'object_type' => $it['object_type'], 'object_id' => $it['object_id'],
                'created' => $it['last'], 'number' => $it['number'],
                'subject' => $it['subject'], 'org' => $it['org'], 'agent' => '',
                'type_name' => (string) ($it['type_name'] ?? ''), 'seconds' => $it['seconds'],
                'billable_seconds' => $it['billable_seconds'] ?? 0,
                'onsite' => $it['onsite'] ?? 0,
                // factor/rate are what the surcharge lines are computed from
                'factor' => (int) ($it['factor'] ?? 100),
                'rate' => (float) ($it['rate'] ?? 0.0),
                'amount' => $it['amount'],
                'billable' => !isset($it['billable']) || $it['billable'],
                'billed' => !empty($it['billed']),
                'is_goodwill' => !empty($it['is_goodwill']),
                'settled' => '0',
            );
        }
        Billing::enrichRows($rows, $config);
        if (isset($_GET['export']) && $_GET['export'] === 'csv') {
            $this->exportCsv($rows, $columns, $config,
                array('date_from'=>'', 'date_to'=>'', 'settled'=>'0')); exit;
        }
        if (isset($_GET['export']) && $_GET['export'] === 'pdf') {
            $this->exportPdf($rows, $columns, $config,
                array('date_from'=>'','date_to'=>'','settled'=>'0'), $__); exit;
        }

        $this->render('overview.tmpl.php', array(
            'orgs'        => Organization::objects()->order_by('name'),
            'searchError' => $searchError,
            'openRows'    => $rows,
            'columns'     => $columns,
            'catalog'     => $catalog,
            'fullColumns' => $fullColumns,
            'addable'     => $this->addableColumns($catalog, $fullColumns),
            'notice'      => $notice,
            'canManage'   => $this->canManage($thisstaff),
            'hideOpenItems' => $config ? (bool) $config->get('hide_open_items') : false,
        ));
    }

    function pageTicket($id) {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        $ticket = Ticket::lookup((int) $id);
        if (!$ticket)
            Http::response(404, $__('Unknown ticket'));
        if (!$thisstaff || !$thisstaff->canAccess($ticket))
            Http::response(403, $__('Access denied'));

        $this->handleObjectPage($ticket, $ticket->getId(), 'T');
    }

    function pageTask($id) {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        $task = Task::lookup((int) $id);
        if (!$task)
            Http::response(404, $__('Unknown task'));
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));

        $this->handleObjectPage($task, $task->getId(), 'A');
    }

    /**
     * Shared logic for the per-ticket / per-task billing page, including the
     * "mark as settled" action.
     */
    private function handleObjectPage($object, $object_id, $object_type) {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        $config  = $this->getPluginConfig();
        $notice  = null;

        if (!$this->requireTimesheet($__))
            return;

        if ($_POST && isset($_POST['do'])) {
            if (!$thisstaff || !$thisstaff->isAdmin() && !($config && $config->get('agent_access'))) {
                Http::response(403, $__('Access denied'));
            }
            // Per-booking settling was removed: a ticket is billed as a whole,
            // not entry by entry. The only per-object action here is toggling
            // the "Kulanz" (goodwill) flag; billed/open uses the shared
            // mark-billed route (also used by the bulk actions elsewhere).
            if ($_POST['do'] === 'toggle_goodwill' && $object_type === 'T') {
                $this->setGoodwill($object_id, !$this->isGoodwill($object_id));
            }
            $self = ROOT_PATH.'scp/dispatcher.php/billing/'.($object_type === 'T' ? 'ticket' : 'task').'/'.$object_id;
            Http::redirect($self);
        }

        $entries = Billing::getEntriesForObject($object_id, $object_type);
        $invoice = Billing::computeInvoice($object_id, $object_type, $config);
        $types   = BillingTimeType::getAll();
        $canBulk = ($thisstaff->isAdmin() || ($config && $config->get('agent_access')))
                 && !$this->billingStatusDisabled();

        $this->render('object.tmpl.php', array(
            'object'       => $object,
            'object_id'    => $object_id,
            'object_type'  => $object_type,
            'entries'      => $entries,
            'invoice'      => $invoice,
            'types'        => $types,
            'notice'       => $notice,
            'canBulk'      => $canBulk,
            'isBilled'     => ($object_type === 'T') ? $this->isBilled($object_id) : false,
            'isGoodwill'   => ($object_type === 'T') ? $this->isGoodwill($object_id) : false,
        ));
    }

    function pageOrg($id = 0) {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));

        if (!$this->requireTimesheet($__))
            return;

        $config = $this->getPluginConfig();
        // The overview and report forms submit the organization as a GET
        // parameter (?id=N); the route also allows /org/N. Accept both.
        if (!$id && isset($_REQUEST['id']))
            $id = (int) $_REQUEST['id'];
        $org    = $id ? Organization::lookup((int) $id) : null;
        // default period for the organization report: current month
        $range  = isset($_REQUEST['range'])
            ? preg_replace('/[^a-z0-9_]/', '', $_REQUEST['range'])
            : (isset($_REQUEST['start']) || isset($_REQUEST['end']) ? '' : 'this_month');
        $start  = isset($_REQUEST['start']) ? Billing::parseDate($_REQUEST['start']) : date('Y-m-01');
        $end    = isset($_REQUEST['end'])   ? Billing::parseDate($_REQUEST['end'])   : date('Y-m-t');
        if ($range !== '' && ($rd = Billing::rangeDates($range)) !== null) {
            // 'all' has no bounds; fall back to a very wide window
            $start = $rd[0] !== '' ? $rd[0] : '1970-01-01';
            $end   = $rd[1] !== '' ? $rd[1] : date('Y-m-d');
        }
        if ($start === '') $start = date('Y-m-01');
        if ($end === '')   $end   = date('Y-m-d');
        $report = ($org) ? Billing::getOrgReport($org->getId(), $start, $end, $config) : null;

        $catalog   = $this->reportCatalog($__);
        $notice    = null;
        $this->handleSaveColumns($catalog, $__, $notice);
        $columns     = $this->loadColumns($catalog);
        $fullColumns = $columns;
        $rows = $this->orgReportRows($report, $org, $config);
        if ($org && isset($_GET['export']) && $_GET['export'] === 'csv') {
            $this->exportCsv($rows, $columns, $config, array(
                'org_id' => $org->getId(),
                'org_name' => $org->getName(), 'date_from' => $start, 'date_to' => $end)); exit;
        }
        if ($org && isset($_GET['export']) && $_GET['export'] === 'pdf') {
            $this->exportPdf($rows, $columns, $config, array('org_id'=>$org->getId(),
                'org_name'=>$org->getName(),
                'date_from'=>$start,'date_to'=>$end,'settled'=>''), $__); exit;
        }

        $footer = $this->footerVars($org, $rows, $config, array(
            'org_name' => $org ? $org->getName() : '', 'date_from' => $start, 'date_to' => $end));
        $footer['footerOrg'] = $org;
        $this->render('org.tmpl.php', array_merge(array(
            'summaryExtra' => array('org_name' => $org ? $org->getName() : '',
                'org' => $org ? $org->getName() : '', 'from' => $start, 'to' => $end),
            'columns'     => $columns,
            'catalog'     => $catalog,
            'fullColumns' => $fullColumns,
            'addable'     => $this->addableColumns($catalog, $fullColumns),
            'notice'      => $notice,
            'canManage'   => $this->canManage($thisstaff),
            'orgRows'     => $rows,
            'orgs'   => Organization::objects()->order_by('name'),
            'org'    => $org,
            'start'  => $start,
            'end'    => $end,
            'range'    => $range,
            'presets'  => Billing::rangePresets($__),
            'report' => $report,
        ), $footer));
    }


    /**
     * Map an organization report onto the flat row structure used by the
     * tables and both exporters. Shared by the organization page and the
     * bulk export so a ZIP document is identical to the single download.
     */
    private function orgReportRows($report, $org, $config) {
        $rows = array();
        if ($report) {
            foreach ($report['tickets'] as $t) {
                $rows[] = array(
                    'object_type' => 'T', 'object_id' => $t['ticket_id'],
                    'created' => '', 'number' => $t['number'], 'subject' => $t['subject'],
                    'org' => $org ? $org->getName() : '', 'agent' => '', 'type_name' => (string) ($t['type_name'] ?? ''),
                    'billed' => !empty($t['billed']), 'is_goodwill' => !empty($t['is_goodwill']),
                    'seconds' => $t['seconds'],
                    'billable_seconds' => $t['billable_seconds'] ?? 0,
                    'onsite' => $t['onsite'] ?? 0,
                    'trips'  => (int) ($t['trips'] ?? 0),
                    'travel' => (float) ($t['travel'] ?? 0.0),
                    'factor' => (int) ($t['factor'] ?? 100),
                    'rate' => (float) ($t['rate'] ?? 0.0), 'amount' => $t['subtotal'],
                    'billable' => true, 'settled' => '',
                );
            }
        }
        Billing::enrichRows($rows, $config);
        return $rows;
    }

    /**
     * Bulk export: pick any number of organizations and download one ZIP that
     * contains a document per organization. File names inside the archive
     * follow the configured export file name pattern, so they match the single
     * exports exactly. The chosen filter values are remembered for next time.
     */
    function pageBulkExport() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));

        $config = $this->getPluginConfig();
        $notice = '';

        // --- remembered filter values ---------------------------------
        $saved = $config ? (string) $config->get('bulk_export_orgs', '') : '';
        $selected = array_values(array_filter(array_map('intval', explode(',', $saved))));
        $range = $config ? (string) $config->get('bulk_export_range', 'this_month') : 'this_month';
        $start = $config ? (string) $config->get('bulk_export_from', '') : '';
        $end   = $config ? (string) $config->get('bulk_export_to', '') : '';
        $fmt   = $config ? (string) $config->get('bulk_export_format', 'pdf') : 'pdf';

        if ($_POST) {
            $selected = (isset($_POST['orgs']) && is_array($_POST['orgs']))
                ? array_values(array_filter(array_map('intval', $_POST['orgs']))) : array();
            $range = isset($_POST['range']) ? preg_replace('/[^a-z0-9_]/', '', $_POST['range']) : '';
            $start = isset($_POST['date_from']) ? Billing::parseDate($_POST['date_from']) : '';
            $end   = isset($_POST['date_to'])   ? Billing::parseDate($_POST['date_to'])   : '';
            $fmt   = (isset($_POST['format']) && in_array($_POST['format'], array('pdf','csv','both'), true))
                ? $_POST['format'] : 'pdf';
            if ($range !== '') {
                $rd = Billing::rangeDates($range);
                if ($rd) { $start = $rd[0]; $end = $rd[1] !== '' ? $rd[1] : date('Y-m-d'); }
            }
            if ($start === '') $start = date('Y-m-01');
            if ($end === '')   $end   = date('Y-m-d');

            // remember the filter for the next visit
            if ($config) {
                $config->set('bulk_export_orgs', implode(',', $selected));
                $config->set('bulk_export_range', $range);
                $config->set('bulk_export_from', $start);
                $config->set('bulk_export_to', $end);
                $config->set('bulk_export_format', $fmt);
            }

            if (isset($_POST['do']) && $_POST['do'] === 'export') {
                if (!$selected)
                    $notice = $__('Please select at least one organization.');
                else
                    $this->streamBulkZip($selected, $start, $end, $fmt, $config, $__);   // exits on success
            }
        }

        if ($start === '' || $end === '') {
            $rd = Billing::rangeDates($range ?: 'this_month');
            if ($rd) { $start = $start ?: $rd[0]; $end = $end ?: ($rd[1] !== '' ? $rd[1] : date('Y-m-d')); }
        }

        $this->render('bulkexport.tmpl.php', array(
            'orgs'     => Organization::objects()->order_by('name'),
            'selected' => $selected,
            'range'    => $range,
            'presets'  => Billing::rangePresets($__),
            'start'    => $start,
            'end'      => $end,
            'format'   => $fmt,
            'notice'   => $notice,
        ));
    }

    /**
     * Build one document per organization and send them as a single ZIP.
     * Organizations without any billable rows are skipped, so the archive
     * never contains empty documents.
     */
    private function streamBulkZip($orgIds, $start, $end, $fmt, $config, $__) {
        $files = array();
        foreach ($orgIds as $oid) {
            $org = Organization::lookup((int) $oid);
            if (!$org) continue;
            $report = Billing::getOrgReport($org->getId(), $start, $end, $config);
            $rows   = $this->orgReportRows($report, $org, $config);
            if (!$rows) continue;                       // nothing to invoice
            $columns = $this->loadColumns($this->reportCatalog($__));
            $filters = array('org_id' => $org->getId(), 'org_name' => $org->getName(),
                'date_from' => $start, 'date_to' => $end, 'settled' => '');
            if ($fmt === 'pdf' || $fmt === 'both')
                $files[$this->exportFileName($filters, 'pdf')] =
                    $this->exportPdf($rows, $columns, $config, $filters, $__, true);
            if ($fmt === 'csv' || $fmt === 'both')
                $files[$this->exportFileName($filters, 'csv')] =
                    $this->exportCsv($rows, $columns, $config, $filters, true);
        }
        if (!$files)
            return;                                     // fall through, page shows again

        $zipName = 'Massen-Export_'.$start.'_'.$end.'.zip';
        $zip = $this->buildZip($files);
        while (ob_get_level()) @ob_end_clean();
        header('Content-Type: application/zip');
        header('Content-Disposition: attachment; filename="'.$zipName.'"');
        header('Content-Length: '.strlen($zip));
        echo $zip;
        exit;
    }

    /**
     * Create a ZIP archive in memory. Uses ZipArchive when the extension is
     * available and otherwise falls back to a minimal STORE-only writer, so
     * the feature also works on hosts without the zip extension.
     */
    private function buildZip(array $files) {
        if (class_exists('ZipArchive')) {
            $tmp = tempnam(sys_get_temp_dir(), 'billing');
            $za  = new ZipArchive();
            if ($za->open($tmp, ZipArchive::CREATE | ZipArchive::OVERWRITE) === true) {
                foreach ($files as $name => $data) $za->addFromString($name, $data);
                $za->close();
                $out = file_get_contents($tmp);
                @unlink($tmp);
                return $out;
            }
            @unlink($tmp);
        }
        // --- fallback: store-only ZIP (no compression) --------------------
        $local = ''; $central = ''; $offset = 0; $count = 0;
        // DOS timestamp so extracted files carry a sane modification date
        $dosTime = (int) (date('H') << 11 | date('i') << 5 | date('s') >> 1);
        $dosDate = (int) ((date('Y') - 1980) << 9 | date('n') << 5 | date('j'));
        foreach ($files as $name => $data) {
            $crc  = crc32($data);
            $len  = strlen($data);
            $hdr  = "\x50\x4b\x03\x04".pack('vvvvv', 20, 0, 0, $dosTime, $dosDate)
                  . pack('VVV', $crc, $len, $len).pack('vv', strlen($name), 0).$name;
            $local .= $hdr.$data;
            $central .= "\x50\x4b\x01\x02".pack('vvvvvv', 20, 20, 0, 0, $dosTime, $dosDate)
                  . pack('VVV', $crc, $len, $len)
                  . pack('vvvvvVV', strlen($name), 0, 0, 0, 0, 0, $offset).$name;
            $offset += strlen($hdr) + $len;
            $count++;
        }
        return $local.$central."\x50\x4b\x05\x06".pack('vvvv', 0, 0, $count, $count)
             . pack('VV', strlen($central), $offset)."\x00\x00";
    }

    /**
     * Save (create/overwrite) or clear an organization's customer note.
     * POSTed from the organization billing page; redirects back afterwards.
     */
    function pageOrgNote() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));

        $orgId  = isset($_POST['org_id']) ? (int) $_POST['org_id'] : 0;
        if ($_POST && isset($_POST['do']) && $_POST['do'] === 'save_note' && $orgId > 0) {
            $by = ($thisstaff && method_exists($thisstaff, 'getName'))
                ? (string) $thisstaff->getName() : '';
            $this->setOrgNote($orgId, isset($_POST['note']) ? $_POST['note'] : '', $by);
        }

        $return = !empty($_POST['return']) ? (string) $_POST['return'] : '';
        // only allow relative, in-app redirect targets
        if ($return === '' || strpos($return, '://') !== false || substr($return, 0, 1) !== '/')
            $return = ROOT_PATH.'scp/dispatcher.php/billing/org?id='.$orgId;
        Http::redirect($return);
    }


    /**
     * Save (create/overwrite) or clear an organization's system checks.
     * POSTed from the organization billing page; redirects back afterwards.
     */
    function pageOrgChecks() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));

        $orgId = isset($_POST['org_id']) ? (int) $_POST['org_id'] : 0;
        if ($_POST && isset($_POST['do']) && $_POST['do'] === 'save_checks' && $orgId > 0) {
            $by = ($thisstaff && method_exists($thisstaff, 'getName'))
                ? (string) $thisstaff->getName() : '';
            $this->setOrgTable($orgId, isset($_POST['tbl']) ? $_POST['tbl'] : array(), $by);
        }

        $return = !empty($_POST['return']) ? (string) $_POST['return'] : '';
        if ($return === '' || strpos($return, '://') !== false || substr($return, 0, 1) !== '/')
            $return = ROOT_PATH.'scp/dispatcher.php/billing/org?id='.$orgId;
        Http::redirect($return);
    }


    /* -- shared column configuration (queue-style, used by all tables) ---- */

    /** True when the plugin runs in time-only (monthly) billing mode. */
    private function timeOnly() {
        $config = $this->getPluginConfig();
        return $config && $config->get('billing_mode') === 'time';
    }

    private function reportCatalog($__) {
        $catalog = array(
            'created' => $__('Entry date'),
            'number'  => $__('Ticket').' / '.$__('Task'),
            'subject' => $__('Subject'),
            'org'     => $__('Organization'),
            'agent'   => $__('Agent'),
            'type'    => $__('Time type'),
            'time'    => $__('Time'),
            'billable_time' => $__('Billable time'),
            'factor'  => $__('Factor'),
            'onsite'  => $__('On-site'),
            'trips'   => $__('Trips'),
            'travel'  => $__('Travel charge'),
            'rate'    => $__('Rate'),
            'amount'  => $__('Amount'),
            'billed'  => $__('Billed'),
            'goodwill' => $__('Goodwill (Kulanz)'),
            'closed'  => $__('Closing date'),
        );
        $catalog['core_user']     = $__('Customer');
        $catalog['core_dept']     = $__('Department');
        $catalog['core_status']   = $__('Status');
        $catalog['core_team']     = $__('Team');
        $catalog['core_assigned'] = $__('Assigned to');
        $catalog['core_tcreated'] = $__('Created');
        $catalog['core_due']      = $__('Due date');
        $catalog['core_updated']  = $__('Last updated');
        foreach (Billing::formFields() as $fid => $flabel)
            $catalog['ff_'.$fid] = $flabel;
        if ($this->timeOnly())
            unset($catalog['rate'], $catalog['amount'], $catalog['travel']);
        if ($this->billingStatusDisabled())
            unset($catalog['billed'], $catalog['goodwill']);
        return $catalog;
    }

    /**
     * True when "Hide open items and disable ticket billing status" is on.
     * In that mode the whole billed/goodwill workflow (Open Items module,
     * mark-as-billed actions, Kulanz flag) is hidden everywhere and tickets
     * are treated as billable automatically - the admin only wants reports
     * and PDF export, not the per-ticket billed-status bookkeeping.
     */
    private function billingStatusDisabled() {
        $config = $this->getPluginConfig();
        return $config && $config->get('hide_open_items');
    }

    /** Handle the shared "save columns" POST (admins only). */
    private function handleSaveColumns($catalog, $__, &$notice) {
        global $thisstaff;
        if (!$_POST || !isset($_POST['do']) || $_POST['do'] !== 'save_columns')
            return;
        if (!$this->canManage($thisstaff))
            Http::response(403, $__('Access denied'));
        $config = $this->getPluginConfig();
        $keys   = isset($_POST['ckey'])   && is_array($_POST['ckey'])   ? $_POST['ckey']   : array();
        $labels = isset($_POST['clabel']) && is_array($_POST['clabel']) ? $_POST['clabel'] : array();
        $widths = isset($_POST['cwidth']) && is_array($_POST['cwidth']) ? $_POST['cwidth'] : array();
        $sort   = isset($_POST['csort'])  && is_array($_POST['csort'])  ? $_POST['csort']  : array();
        $saved  = array(); $seen = array();
        foreach (array_values($keys) as $i => $k) {
            if (!isset($catalog[$k]) || isset($seen[$k])) continue;
            $seen[$k] = true;
            $saved[] = array(
                'key'      => $k,
                'label'    => trim((string) ($labels[$i] ?? '')),
                'width'    => min(800, max(30, (int) ($widths[$i] ?? 100))),
                'sortable' => !empty($sort[$k]) ? 1 : 0,
            );
        }
        if (!empty($_POST['add_key']) && isset($catalog[$_POST['add_key']]) && !isset($seen[$_POST['add_key']]))
            $saved[] = array('key' => $_POST['add_key'], 'label' => '', 'width' => 120, 'sortable' => 1);
        if ($saved && $config) {
            $config->set('report_columns', json_encode($saved));
            $notice = $__('Columns saved.');
        }
    }

    /**
     * Effective columns: stored config or defaults. $supported optionally
     * restricts to keys that make sense for the current table.
     */
    private function loadColumns($catalog, $supported = null) {
        $config  = $this->getPluginConfig();
        $columns = null;
        if ($config && ($json = $config->get('report_columns'))
                && ($a = json_decode($json, true)) && is_array($a)) {
            $columns = array();
            foreach ($a as $c) {
                if (!is_array($c) || !isset($c['key'], $catalog[$c['key']])) continue;
                $columns[] = array(
                    'key'      => $c['key'],
                    'label'    => (isset($c['label']) && trim($c['label']) !== '')
                                    ? $c['label'] : $catalog[$c['key']],
                    'width'    => min(800, max(30, (int) ($c['width'] ?? 100))),
                    'sortable' => !empty($c['sortable']),
                );
            }
            if (!$columns) $columns = null;
        }
        if ($columns === null) {
            // Default column set shipped with the plugin (matches the layout
            // the billing workflow actually needs; "amount"/"rate" are added
            // by the admin when running in money mode).
            $defaults = array(
                'closed'        => 120,
                'number'        => 100,
                'subject'       => 300,
                'org'           => 160,
                'agent'         => 120,
                'type'          => 140,
                'time'          =>  70,
                'billable_time' => 120,
            );
            $columns = array();
            foreach ($defaults as $k => $w)
                if (isset($catalog[$k]))
                    $columns[] = array('key'=>$k, 'label'=>$catalog[$k], 'width'=>$w, 'sortable'=>true);
        }
        // The time type must never silently disappear: older saved column
        // configurations (from before it was a default column) simply do not
        // contain it, which made "Zeitart" vanish - most visibly after
        // switching the billing mode, because that rebuilds the catalogue.
        if (isset($catalog['type']) && !in_array('type', array_column($columns, 'key'))) {
            $ins = array('key'=>'type', 'label'=>$catalog['type'], 'width'=>140, 'sortable'=>true);
            $pos = array_search('agent', array_column($columns, 'key'));
            if ($pos === false) $pos = count($columns) - 1;
            array_splice($columns, $pos + 1, 0, array($ins));
        }
        if (is_array($supported))
            $columns = array_values(array_filter($columns, function ($c) use ($supported) {
                return in_array($c['key'], $supported);
            }));
        return $columns;
    }

    /** Options for "add column" = catalogue keys not currently used. */
    private function addableColumns($catalog, $columns) {
        $addable = array();
        foreach ($catalog as $k => $l)
            if (!in_array($k, array_column($columns, 'key'))) $addable[$k] = $l;
        uasort($addable, function ($a, $b) {
            return strcasecmp(html_entity_decode($a), html_entity_decode($b));
        });
        return $addable;
    }

    /**
     * Customizable report table over all time entries: filters, selectable
     * columns, sortable headers, CSV and PDF export.
     */
    function pageReport() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canUse($thisstaff))
            Http::response(403, $__('Access denied'));
        if (!$this->requireTimesheet($__))
            return;

        $config  = $this->getPluginConfig();
        $filters = array(
            'settled'      => isset($_GET['settled']) && in_array($_GET['settled'], array('0','1'), true) ? $_GET['settled'] : '',
            'otype'        => isset($_GET['otype'])   && in_array($_GET['otype'], array('T','A'), true)   ? $_GET['otype']   : '',
            'org_id'       => isset($_GET['org_id'])  ? (int) $_GET['org_id'] : 0,
            'time_type_id' => isset($_GET['time_type_id']) ? (int) $_GET['time_type_id'] : 0,
            'date_from'    => isset($_GET['date_from']) ? Billing::parseDate($_GET['date_from']) : '',
            'date_to'      => isset($_GET['date_to'])   ? Billing::parseDate($_GET['date_to'])   : '',
            'range'        => isset($_GET['range'])
                ? preg_replace('/[^a-z0-9_]/', '', $_GET['range'])
                // default period: current month (unless dates were given)
                : ((isset($_GET['date_from']) || isset($_GET['date_to'])) ? '' : 'this_month'),
            'sort'         => isset($_GET['sort']) ? $_GET['sort'] : 'created',
            'dir'          => isset($_GET['dir'])  ? $_GET['dir']  : 'desc',
        );

        // a quick-select preset overrides the two date fields
        if ($filters['range'] !== ''
                && ($rd = Billing::rangeDates($filters['range'])) !== null) {
            $filters['date_from'] = $rd[0];
            $filters['date_to']   = $rd[1];
        }

        $catalog = $this->reportCatalog($__);
        $notice  = null;
        $this->handleSaveColumns($catalog, $__, $notice);
        $columns = $this->loadColumns($catalog);

        $rows = Billing::getEntriesReport($filters, $config);

        // Export branches — before any HTML is emitted.
        if (isset($_GET['export']) && $_GET['export'] === 'csv') {
            $this->exportCsv($rows, $columns, $config, $filters);
            exit;
        }
        if (isset($_GET['export']) && $_GET['export'] === 'pdf') {
            $this->exportPdf($rows, $columns, $config, $filters, $__);
            exit;
        }

        // catalogue entries not yet used -> options for "add column"
        $addable = array();
        foreach ($catalog as $k => $l)
            if (!in_array($k, array_column($columns, 'key'))) $addable[$k] = $l;

        // When filtered to a single organization, show the same "section below
        // the table" editor as the organization page, so what gets exported
        // here can also be seen and edited here (otherwise it renders nothing).
        $footerOrg = ($filters['org_id'] > 0 && class_exists('Organization'))
            ? Organization::lookup($filters['org_id']) : null;
        $footer = $this->footerVars($footerOrg, $rows, $config, array(
            'org_name'  => $footerOrg ? $footerOrg->getName() : '',
            'date_from' => $filters['date_from'], 'date_to' => $filters['date_to']));
        $footer['footerOrg'] = $footerOrg;

        $this->render('report.tmpl.php', array_merge(array(
            'rows'      => $rows,
            'columns'     => $columns,
            'catalog'     => $catalog,
            'fullColumns' => $columns,
            'addable'     => $addable,
            'filters'   => $filters,
            'summaryExtra' => array('from' => $filters['date_from'], 'to' => $filters['date_to']),
            'presets'   => Billing::rangePresets($__),
            'notice'    => $notice,
            'canManage' => $this->canManage($thisstaff),
            'orgs'      => Organization::objects()->order_by('name'),
            'timeTypes' => BillingTimeType::getAll(),
        ), $footer));
    }

    /** Format one report cell for exports (shared by CSV and PDF). */
    private function exportCell($col, $row, $config, $__) {
        switch ($col) {
            case 'created': return Billing::formatDate($row['created'] ?? '');
            case 'number':  return '#'.$row['number'];
            case 'subject': return $row['subject'];
            case 'org':     return $row['org'];
            case 'agent':   return $row['agent'];
            case 'type':    return Billing::typeLabel($row['type_name'], $row['factor'] ?? 100);
            case 'time':    return Billing::formatDuration($row['seconds']);
            case 'rate':    return $row['billable'] ? Billing::formatMoney($row['rate'], $config) : '';
            case 'amount':  return Billing::formatMoney($row['amount'], $config);
            case 'settled': return $row['settled'] === '1' ? $__('Settled') : $__('Open');
            case 'billable_time': return Billing::formatDuration((int) ($row['billable_seconds'] ?? 0));
            case 'factor':  return isset($row['factor']) ? ((int) $row['factor']).'%' : '';
            case 'onsite':  return !empty($row['onsite']) ? $__('On-site') : $__('Office');
            case 'trips':   return (string) (int) ($row['trips'] ?? 0);
            case 'travel':  return Billing::formatMoney($row['travel'] ?? 0, $config);
            case 'billed':  return !empty($row['billed']) ? $__('Yes') : $__('No');
            case 'goodwill': return !empty($row['is_goodwill']) ? $__('Yes') : '';
            case 'closed':  return Billing::formatDate($row['closed'] ?? '');
        }
        if (strpos($col, 'ff_') === 0)
            return Billing::formValue($row[$col] ?? '');
        if (strpos($col, 'core_') === 0)
            return (string) ($row[$col] ?? '');
        return '';
    }

    private function exportCsv($rows, $columns, $config, $filters = array(), $return = false) {
        list($__, $_N) = self::translate('billing');
        if (!$return) {
            header('Content-Type: text/csv; charset=UTF-8');
            header('Content-Disposition: attachment; filename="'.$this->exportFileName($filters, 'csv').'"');
        }
        // bulk export collects the bytes instead of streaming them
        $out = $return ? fopen('php://temp', 'w+') : fopen('php://output', 'w');
        fwrite($out, "\xEF\xBB\xBF");                       // UTF-8 BOM for Excel
        $head = array();
        foreach ($columns as $col) $head[] = html_entity_decode($col['label'], ENT_QUOTES, 'UTF-8');
        fputcsv($out, $head, ';');                           // ; = German Excel default
        $sumSecs = 0; $sumBill = 0; $sumAmount = 0.0;
        foreach ($rows as $r) {
            $line = array();
            foreach ($columns as $col) $line[] = $this->exportCell($col['key'], $r, $config, $__);
            fputcsv($out, $line, ';');
            $sumSecs += $r['seconds']; $sumBill += (int)($r['billable_seconds'] ?? 0); $sumAmount += $r['amount'];
        }
        // summary row: identical wording to the tables and the PDF
        $sum = Billing::summaryText($rows, $config, $__, array(
            'org'  => isset($filters['org_name'])  ? $filters['org_name']  : '',
            'from' => isset($filters['date_from']) ? $filters['date_from'] : '',
            'to'   => isset($filters['date_to'])   ? $filters['date_to']   : '',
        ));
        fputcsv($out, array($sum['left'], $sum['right']), ';');

        // Section below the table (per 'export_footer_mode'), single org only.
        $orgId = isset($filters['org_id']) ? (int) $filters['org_id'] : 0;
        $footerMode = $config ? $config->get('export_footer_mode', 'note') : 'note';
        if ($orgId > 0 && $footerMode === 'note') {
            $nd = $this->getOrgNote($orgId);
            $noteText = $nd ? (string) $nd['note'] : '';
            if (trim($noteText) === '')
                $noteText = $config ? (string) $config->get('note_default_text', '') : '';
            if (trim($noteText) !== '') {
                fputcsv($out, array(), ';');                 // blank separator row
                fputcsv($out, array(html_entity_decode($__('Customer note'), ENT_QUOTES, 'UTF-8')), ';');
                foreach (explode("\n", $noteText) as $ln)
                    fputcsv($out, array($ln), ';');
            }
        } elseif ($orgId > 0 && $footerMode === 'checks') {
            $cols  = $this->tableColumnDefs();
            $rdefs = $this->tableRowDefs();
            $tbl   = $this->getOrgTable($orgId);
            $store = is_array($tbl['rows']) ? $tbl['rows'] : array();
            $dec = function ($s) { return html_entity_decode((string) $s, ENT_QUOTES, 'UTF-8'); };
            $dataCols = $cols['data'];
            fputcsv($out, array(), ';');                     // blank separator row
            $title = $this->tableTitle();
            if (trim($title) !== '')
                fputcsv($out, array($dec($title)), ';');
            // configurable "last modified" line at the top
            $metaLine = $this->tableMetaLine($tbl['updated'], $tbl['updated_by'], $this->reportTokenVals($rows, $filters, $config));
            if ($metaLine !== '')
                fputcsv($out, array($dec($metaLine)), ';');
            // header row: name column + data columns
            $header = array($dec($cols['name']));
            foreach ($dataCols as $c) $header[] = $dec($c['label']);
            fputcsv($out, $header, ';');
            // active rows only
            foreach ($rdefs as $rdef) {
                $st = isset($store[$rdef['key']]) && is_array($store[$rdef['key']]) ? $store[$rdef['key']] : array();
                $on = !isset($st['active']) ? true : (bool) $st['active'];
                if (!$on) continue;
                $cells = (isset($st['cells']) && is_array($st['cells'])) ? $st['cells'] : array();
                $line = array($dec($rdef['label']));
                foreach ($dataCols as $c)
                    $line[] = isset($cells[$c['key']]) ? (string) $cells[$c['key']] : '';
                fputcsv($out, $line, ';');
            }
        }
        if ($return) {
            rewind($out);
            $data = stream_get_contents($out);
            fclose($out);
            return $data;
        }
        fclose($out);
    }

    /**
     * Resolve placeholders in the configurable PDF texts, e.g.
     * %{report.org} %{report.from} %{report.to} %{report.total}
     */
    private function pdfTokens($text, $rows, $filters, $config, $__) {
        if ($text === '') return '';
        // Same value set as the table summary. This used to build its own,
        // smaller list, so %{report.onsite}, %{report.office},
        // %{report.subtotal}, %{report.tax} and %{report.tax_rate} stayed
        // unresolved in the title, subtitle, header and footer texts.
        $vals = Billing::reportValues($rows, $config, array(
            'org'  => isset($filters['org_name'])  ? $filters['org_name']  : '',
            'from' => isset($filters['date_from']) ? $filters['date_from'] : '',
            'to'   => isset($filters['date_to'])   ? $filters['date_to']   : '',
        ));
        return Billing::applyTokens($text, $vals);
    }

    /**
     * Resolve the configured logo into something FPDF can read.
     * Returns array($fileOrPath, $explicitType, $tmpFileToUnlink).
     * Modes: none | helpdesk | upload | path
     */
    private function pdfLogo($config) {
        $none = array('', '', '');
        if (!$config)
            return $none;
        $mode = $config->get('pdf_logo_mode', 'none');

        // --- an uploaded file or the helpdesk logo: both are AttachmentFiles
        $file = null;
        if ($mode === 'upload') {
            $fid = $config->get('pdf_logo_file');
            // legacy: the old field stored  fileId => fileName  as JSON
            if (is_string($fid) && $fid !== '' && $fid[0] === '[' || (is_string($fid) && $fid !== '' && $fid[0] === '{')) {
                $d = json_decode($fid, true);
                if (is_array($d)) { $k = array_keys($d); $fid = reset($k); }
            }
            if ((int) $fid && class_exists('AttachmentFile'))
                $file = AttachmentFile::lookup((int) $fid);
        } elseif ($mode === 'helpdesk') {
            global $cfg, $ost;
            $c = $cfg ?: ($ost && method_exists($ost, 'getConfig') ? $ost->getConfig() : null);
            if ($c && method_exists($c, 'getStaffLogo'))
                $file = $c->getStaffLogo();
            if (!$file && $c && method_exists($c, 'getClientLogo'))
                $file = $c->getClientLogo();
            if (!$file) {
                // No custom logo uploaded: scp/logo.php falls back to the
                // shipped default image, so use exactly that file here too.
                $root = defined('ROOT_DIR') ? ROOT_DIR : dirname(dirname(dirname(__DIR__))).'/';
                foreach (array('images/ost-logo.png', 'scp/images/ost-logo.png') as $rel) {
                    $p = rtrim($root, '/\\').'/'.$rel;
                    if (file_exists($p))
                        return array($p, '', '');
                }
                return $none;
            }
        }
        if ($file && method_exists($file, 'getData')) {
            $data = $file->getData();
            if ($data !== '' && $data !== false) {
                $type = method_exists($file, 'getType') ? (string) $file->getType() : '';
                $ext  = stripos($type, 'png') !== false ? 'PNG'
                      : (stripos($type, 'gif') !== false ? 'GIF' : 'JPG');
                $tmp = tempnam(sys_get_temp_dir(), 'billinglogo');
                if ($tmp && @file_put_contents($tmp, $data) !== false)
                    return array($tmp, $ext, $tmp);
            }
            return $none;
        }

        return $none;
    }

    private function exportPdf($rows, $columns, $config, $filters, $__, $return = false) {
        require_once(__DIR__.'/class.BillingPdf.php');
        $enc = function ($s) {
            $s = html_entity_decode((string) $s, ENT_QUOTES, 'UTF-8');
            $c = @iconv('UTF-8', 'CP1252//TRANSLIT', $s);
            return $c !== false ? $c : $s;
        };

        $orient = $config ? $config->get('pdf_orientation', 'L') : 'L';
        $size   = $config ? $config->get('pdf_page_size', 'A4')  : 'A4';
        if (!in_array($orient, array('L', 'P'), true))          $orient = 'L';
        if (!in_array($size, array('A4', 'Letter', 'A3'), true)) $size  = 'A4';

        $pdf = new BillingPdf($orient, 'mm', $size);
        $pdf->bEncoder = $enc;

        // --- configurable letterhead --------------------------------
        $pdf->bTitle = $config ? trim((string) $config->get('pdf_title')) : '';
        if ($pdf->bTitle === '')
            $pdf->bTitle = $__('Time & Billing').' - '.$__('Report');
        $pdf->bTitle    = $this->pdfTokens($pdf->bTitle, $rows, $filters, $config, $__);
        $pdf->bSubtitle = $this->pdfTokens(
            $config ? trim((string) $config->get('pdf_subtitle')) : '', $rows, $filters, $config, $__);
        // one shared alignment drives title, subtitle, header and footer
        $textAlign = $config ? $config->get('pdf_text_align', 'left') : 'left';
        if (!in_array($textAlign, array('left','center','right'), true)) $textAlign = 'left';
        $pdf->bTextAlign = $textAlign;
        $alignBlock = function ($html, $align) {
            $html = (string) $html;
            if ($html === '' || $align === 'left') return $html;
            $css = $align === 'center' ? 'center' : 'right';
            return '<div style="text-align:'.$css.'">'.$html.'</div>';
        };
        $pdf->bHeaderText = $alignBlock($this->pdfTokens(
            $config ? (string) $config->get('pdf_header_text') : '', $rows, $filters, $config, $__), $textAlign);
        $pdf->bFooterText = $alignBlock($this->pdfTokens(
            $config ? (string) $config->get('pdf_footer_text') : '', $rows, $filters, $config, $__), $textAlign);
        list($logoFile, $logoType, $logoTmp) = $this->pdfLogo($config);
        $pdf->bLogo       = $logoFile;
        $pdf->bLogoType   = $logoType;

        // --- layout: one setting drives logo placement and text flow ----
        $layout = $config ? $config->get('pdf_layout', 'logo_left') : 'logo_left';
        $map = array(
            'logo_left'   => array('left',   true),
            'logo_top'    => array('left',   false),
            'logo_center' => array('center', false),
            'logo_right'  => array('right',  true),
        );
        if (!isset($map[$layout])) $layout = 'logo_left';
        list($logoAlign, $beside) = $map[$layout];
        $pdf->bLogoAlign  = $logoAlign;
        $pdf->bBesideLogo = $beside;

        $pdf->bPageNumbers = $config ? (bool) $config->get('pdf_page_numbers', true) : true;
        $pdf->bPageLabel   = $__('Page').' %s / %s';

        if (!$config || $config->get('pdf_show_meta', true)) {
            // localized (German by default) instead of raw ISO values
            $meta = $__('Generated').': '.Billing::formatDateTime(date('Y-m-d H:i:s'));
            if (!empty($filters['date_from']) || !empty($filters['date_to']))
                $meta .= '   '.$__('From').': '.(!empty($filters['date_from']) ? Billing::formatDate($filters['date_from']) : '-')
                       .'  '.$__('To').': '.(!empty($filters['date_to']) ? Billing::formatDate($filters['date_to']) : '-');
            if (isset($filters['settled']) && $filters['settled'] !== '')
                $meta .= '   '.($filters['settled'] === '1' ? $__('Settled') : $__('Open'));
            $pdf->bMeta = $meta;
        }

        // --- column widths scaled onto the page ---------------------
        $pageW = ($orient === 'L' ? 297 : 210);
        if ($size === 'Letter') $pageW = ($orient === 'L' ? 279 : 216);
        if ($size === 'A3')     $pageW = ($orient === 'L' ? 420 : 297);
        $pageW -= 20;                                        // margins
        $totalPx = 0;
        // The time type now carries its factor ("Feiertag (200 %)"), which no
        // longer fits the default width - give that column a sensible minimum
        // so the factor is not cut off in the PDF.
        $minPx = function ($col) {
            $w = max(30, (int) $col['width']);
            if ($col['key'] === 'type')
                $w = max($w, 170);
            return $w;
        };
        foreach ($columns as $col) $totalPx += $minPx($col);
        $scale = $totalPx > 0 ? $pageW / $totalPx : 1;
        $w = array();
        foreach ($columns as $col)
            $w[$col['key']] = max(12, $minPx($col) * $scale);

        $pdf->bColumns = $columns;
        $pdf->bWidths  = $w;
        $pdf->AliasNbPages();
        $pdf->SetAutoPageBreak(true, 16);
        $pdf->AddPage();                                     // draws the letterhead + head row

        $trim = function ($s, $mm) use ($pdf) {              // clip text to cell width
            while ($s !== '' && $pdf->GetStringWidth($s) > $mm - 2)
                $s = substr($s, 0, -1);
            return $s;
        };

        $sumSecs = 0; $sumBill = 0; $sumAmount = 0.0;
        foreach ($rows as $r) {
            foreach ($columns as $col) {
                $c = $col['key'];
                $align = 'C';
                $val = $this->exportCell($c, $r, $config, $__);
                // The factor must survive: if the label is too wide, shorten
                // the NAME and keep the "(200 %)" suffix, instead of letting
                // the generic trim cut the factor off the end.
                if ($c === 'type' && preg_match('/^(.*) (\(\d+ %\))$/u', $val, $m)) {
                    $suffix = ' '.$m[2];
                    $room   = $w[$c] - $pdf->GetStringWidth($enc($suffix)) - 2;
                    $val    = $trim($enc($m[1]), max(6, $room)).$suffix;
                    $pdf->Cell($w[$c], 5.5, $val, 1, 0, $align);
                    continue;
                }
                $pdf->Cell($w[$c], 5.5, $trim($enc($val), $w[$c]), 1, 0, $align);
            }
            $pdf->Ln();
            $sumSecs += $r['seconds']; $sumBill += (int) ($r['billable_seconds'] ?? 0); $sumAmount += $r['amount'];
        }

        // One continuous summary row: configurable left and right text, no
        // inner borders, identical wording to the tables and the CSV export.
        $sum = Billing::summaryText($rows, $config, $__, array(
            'org'  => isset($filters['org_name'])  ? $filters['org_name']  : '',
            'from' => isset($filters['date_from']) ? $filters['date_from'] : '',
            'to'   => isset($filters['date_to'])   ? $filters['date_to']   : '',
        ));
        $totalW = 0;
        foreach ($columns as $col) $totalW += $w[$col['key']];
        $half = $totalW / 2;
        $pdf->SetFont('Helvetica', 'B', 8);
        $pdf->SetFillColor(238, 238, 238);
        $pdf->Cell($half, 6, $trim($enc($sum['left']),  $half), 'LTB', 0, 'L', true);
        $pdf->Cell($half, 6, $trim($enc($sum['right']), $half), 'RTB', 0, 'R', true);
        $pdf->Ln();

        // Totals block under the table - subtotal/tax/total in money mode,
        // total/billable time in time-billing mode (same as the web views).
        $totals = Billing::totalsBlock($rows, $config, $__);
        if ($totals) {
            $pdf->Ln(2);
            $n = count($totals); $i = 0;
            foreach ($totals as $tLabel => $tValue) {
                $i++;
                $pdf->SetFont('Helvetica', ($i === $n) ? 'B' : '', 9);
                $line = $tLabel.': '.$tValue;
                $pdf->Cell($totalW, 5, $trim($enc($line), $totalW), 0, 1, 'R');
            }
        }

        // Section below the table, per the 'export_footer_mode' setting and
        // scoped to a single organization: a free-text customer note, or the
        // fixed system-check list. 'off' prints nothing.
        $orgId = isset($filters['org_id']) ? (int) $filters['org_id'] : 0;
        $footerMode = $config ? $config->get('export_footer_mode', 'note') : 'note';
        if ($orgId > 0 && $footerMode === 'note') {
            $nd = $this->getOrgNote($orgId);
            $noteText = $nd ? (string) $nd['note'] : '';
            if (trim($noteText) === '')                       // fall back to the configured default
                $noteText = $config ? (string) $config->get('note_default_text', '') : '';
            if (trim($noteText) !== '') {
                $pdf->Ln(5);
                $pdf->SetFont('Helvetica', 'B', 9);
                $pdf->SetFillColor(238, 238, 238);
                $pdf->Cell($totalW, 6, $trim($enc($__('Customer note')), $totalW), 'LTRB', 1, 'L', true);
                $pdf->SetFont('Helvetica', '', 9);
                // MultiCell wraps long text and honours the note's own line breaks.
                $pdf->MultiCell($totalW, 4.8, $enc($noteText), 'LRB', 'L');
            }
        } elseif ($orgId > 0 && $footerMode === 'checks') {
            $cols  = $this->tableColumnDefs();
            $rdefs = $this->tableRowDefs();
            $tbl   = $this->getOrgTable($orgId);
            $store = is_array($tbl['rows']) ? $tbl['rows'] : array();
            // only rows that are switched on are exported
            $active = array();
            foreach ($rdefs as $rdef) {
                $st = isset($store[$rdef['key']]) && is_array($store[$rdef['key']]) ? $store[$rdef['key']] : array();
                $on = !isset($st['active']) ? true : (bool) $st['active'];
                if ($on) $active[] = array('label' => $rdef['label'],
                    'cells' => (isset($st['cells']) && is_array($st['cells'])) ? $st['cells'] : array());
            }
            $dataCols = $cols['data'];
            $nData = count($dataCols);
            if ($active && ($nData > 0 || $cols['name'] !== '')) {
                $pdf->Ln(5);
                $title = $this->tableTitle();
                if (trim($title) !== '') {
                    $pdf->SetFont('Helvetica', 'B', 10);
                    $pdf->Cell($totalW, 6, $trim($enc($title), $totalW), 0, 1, 'L');
                }
                // bold, configurable "last modified" line at the top
                $metaLine = $this->tableMetaLine($tbl['updated'], $tbl['updated_by'], $this->reportTokenVals($rows, $filters, $config));
                if ($metaLine !== '') {
                    $pdf->SetFont('Helvetica', 'B', 8);
                    $pdf->MultiCell($totalW, 4.8, $enc($metaLine), 0, 'L');
                    $pdf->Ln(1);
                }
                $nameW = $nData > 0 ? $totalW * 0.30 : $totalW;
                $dataW = $nData > 0 ? ($totalW - $nameW) / $nData : 0;
                // header row
                $pdf->SetFont('Helvetica', 'B', 8);
                $pdf->SetFillColor(238, 238, 238);
                $pdf->Cell($nameW, 6, $trim($enc($cols['name']), $nameW), 1, $nData ? 0 : 1, 'L', true);
                foreach ($dataCols as $i => $c)
                    $pdf->Cell($dataW, 6, $trim($enc($c['label']), $dataW), 1, ($i === $nData - 1) ? 1 : 0, 'C', true);
                // body rows
                $pdf->SetFont('Helvetica', '', 8);
                foreach ($active as $ar) {
                    $pdf->Cell($nameW, 5.5, $trim($enc($ar['label']), $nameW), 1, $nData ? 0 : 1, 'L');
                    foreach ($dataCols as $i => $c) {
                        $val = isset($ar['cells'][$c['key']]) ? (string) $ar['cells'][$c['key']] : '';
                        $pdf->Cell($dataW, 5.5, $trim($enc($val), $dataW), 1, ($i === $nData - 1) ? 1 : 0, 'L');
                    }
                }
            }
        }

        $out = $pdf->Output('S');
        if (!empty($logoTmp)) @unlink($logoTmp);

        if ($return) return $out;      // bulk export collects the bytes
        header('Content-Type: application/pdf');
        header('Content-Disposition: attachment; filename="'.$this->exportFileName($filters, 'pdf').'"');
        echo $out;
    }

    /**
     * Upload endpoint for the PDF logo.
     *
     * osTicket's own ajax.php/form/upload/<id> requires a DynamicFormField row
     * in the database; plugin config fields have none, so it replies with
     * "400 No such field". We run the very same FileUploadField::ajaxUpload()
     * here against an ad-hoc field, which registers the file in the session
     * exactly like the core endpoint does.
     */
    function pageUploadFile() {
        global $thisstaff;
        header('Content-Type: application/json; charset=UTF-8');
        if (!$this->canManage($thisstaff))
            Http::response(403, 'Access denied');
        if (empty($_FILES['upload']) || !is_uploaded_file($_FILES['upload']['tmp_name']))
            Http::response(400, 'No file');
        if (!class_exists('AttachmentFile'))
            Http::response(500, 'Uploads unavailable');

        // NOTE: AttachmentFile::format() expects the MULTI-file shape
        // ($_FILES['x']['name'] being an array, as produced by name="logo[]").
        // We post a single file, so its keys are plain strings and format()
        // would return an empty list. Validate here and hand the single-file
        // array straight to uploadLogo(), which is what it expects.
        $file = $_FILES['upload'];
        if (!empty($file['error'])) {
            echo JsonDataEncoder::encode(array('error' => 'upload error #'.$file['error']));
            exit;
        }
        $file['name'] = Format::sanitize($file['name']);
        if (!$file['name']) {
            echo JsonDataEncoder::encode(array('error' => 'invalid file name'));
            exit;
        }

        // uploadLogo() stores the image with file type "L", exactly like
        // osTicket's own logo setting, so it is skipped by deleteOrphans().
        // Aspect ratio 0 disables the "image is too square" restriction.
        //
        // It returns the file OBJECT (AttachmentFile::create() -> $f), not an
        // id. Casting an object to int yields 1, which is why uploads used to
        // end up pointing at file #1 ("powered-by-osticket.png").
        $err = '';
        $f = AttachmentFile::uploadLogo($file, $err, 0);
        if (!$f) {
            echo JsonDataEncoder::encode(array('error' => $err ?: 'upload rejected'));
            exit;
        }
        $id = 0;
        if (is_object($f)) {
            if (method_exists($f, 'getId')) $id = (int) $f->getId();
            elseif (isset($f->id))          $id = (int) $f->id;
        } else {
            $id = (int) $f;
        }
        if ($id <= 0) {
            echo JsonDataEncoder::encode(array('error' => 'stored, but no file id returned'));
            exit;
        }
        echo JsonDataEncoder::encode(array('id' => $id, 'name' => $file['name']));
        exit;
    }

    /**
     * Reliable server-side endpoint to assign a ticket's time type from the
     * billing panel on the ticket page. A plain form POST (with CSRF token)
     * instead of fragile JS injection into the reply form.
     */
    function pageSetType($id) {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        $ticketId = (int) $id;
        if (!$thisstaff || !$this->canUse($thisstaff))
            Http::response(403, 'Access denied');

        $types   = BillingTimeType::getActiveList();
        $current = (int) $this->getTicketType($ticketId);
        if ($current <= 0) {
            $cfg = $this->getPluginConfig();
            $current = $cfg ? (int) $cfg->get('default_time_type') : 0;
        }

        // POST -> save and answer with 201 JSON so osTicket's dialog updates
        // the inline value in place (mirrors ajax.tickets.php::editField).
        if ($_POST && isset($_POST['billing_time_type'])) {
            $newId = (int) $_POST['billing_time_type'];
            // remember the previous state so the ticket note can show what changed
            $oldId    = (int) $this->getTicketType($ticketId);
            $oldTrips = (int) $this->getTicketTrips($ticketId);
            $this->setTicketType($ticketId, $newId);
            $newTrips = $oldTrips;
            if (isset($_POST['billing_trips'])) {
                $newTrips = max(0, (int) $_POST['billing_trips']);
                $this->setTicketTrips($ticketId, $newTrips);
            }
            $changes = array();
            if ($oldId !== $newId) {
                $oldName = isset($types[$oldId]) ? $types[$oldId]->getName() : $__('None');
                $newName = isset($types[$newId]) ? $types[$newId]->getName() : $__('None');
                $changes[] = $__('Time type').': '.$oldName.' -> '.$newName;
            }
            if ($oldTrips !== $newTrips)
                $changes[] = $__('Trips').': '.$oldTrips.' -> '.$newTrips;
            $reason = isset($_POST['billing_reason']) ? trim((string) $_POST['billing_reason']) : '';
            if ($changes || $reason !== '')
                $this->logTicketBillingChange($ticketId,
                    $changes ? implode("\n", $changes) : $__('Billing details confirmed'), $reason);
            $label = isset($types[$newId]) ? $types[$newId]->getName() : $__('None');
            if (isset($types[$newId])) {
                $f = (int) $types[$newId]->getFactor();
                if ($f !== 100) $label .= ' ('.$f.'%)';
            }
            $trips = (int) $this->getTicketTrips($ticketId);
            if ($trips > 0) $label .= '  -  '.sprintf($__('Trips: %d'), $trips);
            Http::response(201, json_encode(array(
                'id'    => 'billing_tt',
                'value' => Format::htmlchars($label),
                'msg'   => $__('Billing time type updated'),
            )));
            return;
        }

        // GET -> render the dialog body (native osTicket popup structure).
        $action = ROOT_PATH.'scp/dispatcher.php/billing/settype/'.$ticketId;
        $title  = sprintf($__('Ticket #%s: Billing time type'), $ticketId);
        header('Content-Type: text/html; charset=UTF-8');
        echo '<h3 class="drag-handle">'.Format::htmlchars($title).'</h3>'
           . '<b><a class="close" href="#"><i class="icon-remove-circle"></i></a></b>'
           . '<div class="clear"></div><hr/>'
           . '<div style="display:block; margin:5px;">'
           . '<form method="post" id="billing-tt-form" action="'.$action.'">'
           . '<table width="100%"><tbody><tr><td>'
           . '<strong>'.Format::htmlchars($__('Billing time type')).':</strong><br>'
           . '<select name="billing_time_type" style="min-width:260px; margin-top:6px;">';
        foreach ($types as $tid => $tt) {
            $lbl = $tt->getName();
            $f   = (int) $tt->getFactor();
            if ($f !== 100) $lbl .= ' ('.$f.'%)';
            $sel = ((int) $tid === $current) ? ' selected' : '';
            echo '<option value="'.(int) $tid.'"'.$sel.'>'.Format::htmlchars($lbl).'</option>';
        }
        echo '</select>'
           . '<div style="margin-top:12px;"><strong>'.Format::htmlchars($__('Trips (call-outs)')).':</strong><br>'
           . '<input type="number" name="billing_trips" min="0" step="1" value="'.(int) $this->getTicketTrips($ticketId).'" style="width:120px; margin-top:6px;">'
           . '<br><small>'.Format::htmlchars($__('Number of trips to bill (uses the travel flat fee of the time type).')).'</small></div>'
           . '<div style="margin-top:14px;">'
           . '<textarea name="billing_reason" rows="3" class="thread-entry" style="width:100%; box-sizing:border-box;"'
           . ' placeholder="'.Format::htmlchars($__('Optional reason for the update')).'"></textarea></div>'
           . '</td></tr></tbody></table><hr>'
           . '<p class="full-width">'
           . '<span class="buttons pull-left">'
           . '<input type="button" name="cancel" class="close" value="'.Format::htmlchars($__('Cancel')).'">'
           . '</span>'
           . '<span class="buttons pull-right">'
           . '<input type="submit" value="'.Format::htmlchars($__('Update')).'">'
           . '</span></p>'
           . '</form></div><div class="clear"></div>'
           // Handle submit ourselves so it posts to the dispatcher route (the
           // native dialog would post to ajax.php). We stop propagation so the
           // dialog's own submit handler does not also fire.
           . '<script type="text/javascript">(function(){'
           . 'var f=document.getElementById("billing-tt-form");'
           . 'if(!f) return;'
           . '$(f).off("submit.billing").on("submit.billing", function(e){'
           . '  e.preventDefault(); e.stopImmediatePropagation();'
           . '  $.ajax({type:"POST", url:f.getAttribute("action"), data:$(f).serialize(),'
           . '    success:function(resp,st,xhr){'
           . '      var o={}; try{o=$.parseJSON(resp);}catch(err){}'
           . '      if(o.id && o.value){ $("#field_"+o.id).html(o.value); }'
           . '      if($.toggleOverlay) $.toggleOverlay(false);'
           . '      $(".dialog#popup").hide(); $(".dialog#popup .body").empty();'
           . '    }});'
           . '  return false;'
           . '});'
           . '})();</script>';
        return;
    }

    /**
     * Proxy a jQuery UI datepicker locale file from include/i18n/{lang}/js/,
     * which osTicket ships but never serves (include/.htaccess denies all
     * direct web access). Only known, existing files are served; anything
     * else - unknown languages, path traversal attempts - gets a quiet 204.
     */
    function pageDpLocale($lang) {
        global $thisstaff;
        if (!$thisstaff)
            Http::response(403, 'Access denied');
        // whitelist: plain language code, no path separators
        if (!preg_match('/^[a-zA-Z]{2,3}(_[a-zA-Z]{2,4})?$/', $lang)) {
            Http::response(204, '');
            return;
        }
        $path = INCLUDE_DIR.'i18n/'.$lang.'/js/jquery.ui.datepicker.js';
        $real = realpath($path);
        $base = realpath(INCLUDE_DIR.'i18n');
        if (!$real || !$base || strpos($real, $base) !== 0 || !is_file($real)) {
            Http::response(204, '');
            return;
        }
        header('Content-Type: application/javascript; charset=UTF-8');
        header('Cache-Control: public, max-age=86400');
        readfile($real);
        exit;
    }

    /**
     * Mark one or more tickets as billed / open again. Used both by the
     * single "Mark as billed" button on the invoice page and by the bulk
     * action bar on every listing view (Open Items, Report, Organization).
     * Redirects back to wherever the form was submitted from.
     */
    function pageMarkBilled() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        $config = $this->getPluginConfig();
        $allowed = $thisstaff && ($thisstaff->isAdmin() || ($config && $config->get('agent_access')));
        if (!$allowed || $this->billingStatusDisabled())
            Http::response(403, $__('Access denied'));

        $ids = array();
        if (!empty($_POST['ticket_ids']) && is_array($_POST['ticket_ids']))
            $ids = array_map('intval', $_POST['ticket_ids']);
        elseif (!empty($_POST['ticket_id']))
            $ids = array((int) $_POST['ticket_id']);
        $billed = !empty($_POST['billed']) ? 1 : 0;

        // Saving per-row trip counts ("Anfahrten") is posted by the same form,
        // distinguished by do=save_trips so it never touches the billed status.
        if (isset($_POST['do']) && $_POST['do'] === 'save_trips') {
            if (!empty($_POST['ttrips']) && is_array($_POST['ttrips'])) {
                list($__, $_N) = self::translate('billing');
                foreach ($_POST['ttrips'] as $tid => $n) {
                    $tid = (int) $tid; $n = max(0, (int) $n);
                    $before = (int) $this->getTicketTrips($tid);
                    if ($before === $n) continue;          // only log real changes
                    $this->setTicketTrips($tid, $n);
                    $this->logTicketBillingChange($tid, $__('Trips').': '.$before.' -> '.$n);
                }
            }
        } else {
            $this->setBilled($ids, $billed);
        }

        $return = !empty($_POST['return']) ? (string) $_POST['return'] : ROOT_PATH.'scp/dispatcher.php/billing';
        // only allow relative, in-app redirect targets
        if (strpos($return, '://') !== false || substr($return, 0, 1) !== '/')
            $return = ROOT_PATH.'scp/dispatcher.php/billing';
        Http::redirect($return);
    }

    /**
     * Diagnostics: shows the timings recorded by timeStart()/timeEnd() for the
     * plugin's own signal handlers, together with the total request duration.
     * Exists because on IIS setups neither the PHP error log nor osTicket's
     * system log is reliably available - this page always works.
     */
    function pageDiag() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        if (!$this->canManage($thisstaff))
            Http::response(403, $__('Only administrators can manage time types.'));
        // Diagnostics are opt-in - when switched off the page is not reachable.
        if (!self::diagEnabled()) {
            Http::redirect(ROOT_PATH.'scp/dispatcher.php/billing');
            return;
        }
        if (isset($_GET['clear'])) {
            unset($_SESSION['billing_perf']);
            Http::redirect(ROOT_PATH.'scp/dispatcher.php/billing/diag');
            return;
        }
        $rows = (isset($_SESSION['billing_perf']) && is_array($_SESSION['billing_perf']))
            ? array_reverse($_SESSION['billing_perf']) : array();
        $idx = false;
        if (Billing::timesheetAvailable()) {
            $r = db_query("SHOW INDEX FROM `".BILLING_TIMESHEET_TABLE."` WHERE Key_name = 'billing_obj'", false);
            $idx = ($r && db_num_rows($r) > 0);
        }
        $this->render('diag.tmpl.php', array(
            'perfRows'  => $rows,
            'hasIndex'  => $idx,
            'notice'    => null,
        ));
    }

    /**
     * Park the time type chosen on the CREATE form.
     *
     * The picker lives next to the Time Recording timer, which is not
     * necessarily inside the form that gets submitted - and a field outside
     * <form> is never sent by the browser. Instead of fighting the DOM, the
     * selection is reported here directly and kept in the session until
     * ticket.created picks it up.
     */
    function pageSetPending($id) {
        global $thisstaff;
        if (!$thisstaff)
            Http::response(403, 'Access denied');
        $typeId = (int) $id;
        if ($typeId > 0)
            $_SESSION['billing_pending_ticket'] = array(0 => $typeId);
        header('Content-Type: text/plain; charset=UTF-8');
        echo 'ok';
        exit;
    }

    function pageTimeTypes() {
        global $thisstaff;
        list($__, $_N) = self::translate('billing');
        $config = $this->getPluginConfig();
        if (!$this->canManage($thisstaff))
            Http::response(403, $__('Only administrators can manage time types.'));

        $notice = null;
        if ($_POST && isset($_POST['do'])) {
            switch ($_POST['do']) {
                case 'add':
                    $tt = BillingTimeType::create(array(
                        'name'        => trim($_POST['name'] ?? ''),
                        'hourly_rate' => (float) str_replace(',', '.', $_POST['hourly_rate'] ?? '0'),
                        'factor'      => max(1, (int) ($_POST['factor'] ?? 100)),
                        'travel_fee'  => (float) str_replace(',', '.', $_POST['travel_fee'] ?? '0'),
                        'billable'    => !empty($_POST['billable']) ? 1 : 0,
                        'isdefault'   => 0,
                        'sort'        => (int) ($_POST['sort'] ?? 0),
                        'isactive'    => 1,
                    ));
                    if ($tt->getName() === '')
                        $notice = $__('Please provide a name for the time type.');
                    elseif ($tt->save())
                        $notice = $__('Time type added.');
                    else
                        $notice = $__('The time type could not be saved. Please try again.');
                    break;

                case 'save':
                    $rates    = $_POST['rate']     ?? array();
                    $factors  = $_POST['tfactor']  ?? array();
                    $travels  = $_POST['ttravel']  ?? array();
                    $names    = $_POST['tname']    ?? array();
                    $billable = $_POST['tbill']    ?? array();
                    $active   = $_POST['tactive']  ?? array();
                    foreach (BillingTimeType::getAll() as $tid => $tt) {
                        if (isset($names[$tid]))
                            $tt->set('name', trim($names[$tid]));
                        if (isset($rates[$tid]))
                            $tt->set('hourly_rate', (float) str_replace(',', '.', $rates[$tid]));
                        if (isset($factors[$tid]))
                            $tt->set('factor', max(1, (int) $factors[$tid]));
                        if (isset($travels[$tid]))
                            $tt->set('travel_fee', (float) str_replace(',', '.', $travels[$tid]));
                        $tt->set('billable', !empty($billable[$tid]) ? 1 : 0);
                        // never deactivate the default type
                        $tt->set('isactive', ($tt->isDefault() || !empty($active[$tid])) ? 1 : 0);
                        $tt->save();
                    }
                    // Persist the chosen default time type (pre-selected in the
                    // ticket forms) into the plugin configuration.
                    if (isset($_POST['default_type']) && $config) {
                        $defId = (int) $_POST['default_type'];
                        if ($defId > 0) {
                            $config->set('default_time_type', $defId);
                        }
                    }
                    // Merged action: also delete any rows whose "Delete" box is
                    // ticked, so a single "Save" button covers edits + removals.
                    $delIds = (isset($_POST['tdelete']) && is_array($_POST['tdelete']))
                        ? array_values(array_filter(array_map('intval',
                            array_keys(array_filter($_POST['tdelete']))),
                            function ($v) { return $v > 0; }))
                        : array();
                    $deleted = 0;
                    if ($delIds) {
                        $in    = implode(',', $delIds);
                        $defId = $config ? (int) $config->get('default_time_type') : 0;
                        $guard = 'isdefault = 0'.($defId > 0 ? ' AND id <> '.$defId : '');
                        db_query('DELETE FROM `'.BILLING_TIME_TYPE_TABLE.'` '
                               . 'WHERE id IN ('.$in.') AND '.$guard, false);
                        $deleted = (int) db_affected_rows();
                        if ($deleted > 0)
                            db_query('DELETE FROM `'.TABLE_PREFIX.'billing_ticket_type` '
                                   . 'WHERE time_type_id IN ('.$in.')', false);
                    }
                    $notice = $deleted > 0
                        ? $__('Time types saved.').' '.sprintf($__('%d deleted.'), $deleted)
                        : $__('Time types saved.');
                    break;

                case 'delete':
                    $ids = (isset($_POST['tdelete']) && is_array($_POST['tdelete']))
                        ? array_map('intval', array_keys(array_filter($_POST['tdelete'])))
                        : array();
                    $ids = array_values(array_filter($ids, function ($v) { return $v > 0; }));
                    $deleted = 0;
                    if ($ids) {
                        $in    = implode(',', $ids);
                        $defId = $config ? (int) $config->get('default_time_type') : 0;
                        // Straight SQL: the ORM path (lookup + affected-rows)
                        // failed silently. The default type is protected in the
                        // statement itself.
                        $guard = 'isdefault = 0'.($defId > 0 ? ' AND id <> '.$defId : '');
                        db_query('DELETE FROM `'.BILLING_TIME_TYPE_TABLE.'` '
                               . 'WHERE id IN ('.$in.') AND '.$guard, false);
                        $deleted = (int) db_affected_rows();
                        if ($deleted > 0)
                            db_query('DELETE FROM `'.TABLE_PREFIX.'billing_ticket_type` '
                                   . 'WHERE time_type_id IN ('.$in.')', false);
                    }
                    $notice = $deleted > 0
                        ? sprintf($_N('%d time type deleted.', '%d time types deleted.', $deleted), $deleted)
                        : $__('No time types were deleted. The default type cannot be removed.');
                    break;
            }
            // A create/update/save just happened - drop the per-request cache
            // so the list below reflects the new state without a page reload.
            BillingTimeType::flushCache();
        }

        $this->render('timetypes.tmpl.php', array(
            'types'  => BillingTimeType::getAll(),
            'currentDefault' => $config ? (int) $config->get('default_time_type') : 0,
            'notice' => $notice,
        ));
    }
}

/*
 * Dependencies are loaded here, AFTER BillingPlugin is declared. If any of
 * these were to fail to load, the class is already defined, so osTicket will
 * never mark this plugin "defunct — missing"; a dependency problem would
 * instead surface as a normal, debuggable error at the point of use.
 */
require_once(__DIR__.'/config.php');              // BillingConfig (needed by getConfig)
require_once(__DIR__.'/class.BillingTimeType.php');
require_once(__DIR__.'/class.Billing.php');
