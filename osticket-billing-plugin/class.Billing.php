<?php
/*********************************************************************
    class.Billing.php

    Core helper for the Time Billing plugin.

    Reads the time entries recorded by the Time Recording plugin
    (table `ost_timesheet`) and turns them into billing summaries and
    invoices. Writes back only the `settled` flag when an entry is billed.

    Data model of ost_timesheet (owned by the Time Recording plugin):
        id, thread_id, object_id, object_type ('T' ticket | 'A' task),
        thread_entry_id, staff_id, time (seconds), time_type_id,
        settled (enum '1'/'0'), created, updated

    PHP 8.4 compatible.
**********************************************************************/

if (!defined('INCLUDE_DIR')) die('Access Denied');

if (!defined('BILLING_TIMESHEET_TABLE'))
    define('BILLING_TIMESHEET_TABLE', TABLE_PREFIX.'timesheet');

class Billing {

    /* -- environment ------------------------------------------------------ */

    /**
     * True when the Time Recording plugin's timesheet table exists.
     */
    static function timesheetAvailable() {
        static $available = null;
        if ($available === null) {
            $res = db_query("SHOW TABLES LIKE '".BILLING_TIMESHEET_TABLE."'", false);
            $available = ($res && db_num_rows($res) > 0);
        }
        return $available;
    }

    /**
     * Cheap cached check whether a table exists (defensive joins).
     */
    static function tableExists($table) {
        static $cache = array();
        if (!isset($cache[$table])) {
            $res = db_query("SHOW TABLES LIKE '".$table."'", false);
            $cache[$table] = ($res && db_num_rows($res) > 0);
        }
        return $cache[$table];
    }

    /**
     * Translate a message through the plugin's own 'billing' text domain, so
     * strings produced in this helper are covered by i18n/<locale> too.
     */
    static function __t($msgid) {
        static $__ = null;
        if ($__ === null) {
            if (class_exists('Plugin') && method_exists('Plugin', 'translate')) {
                list($__, ) = Plugin::translate('billing');
            } else {
                // Fallback for non-plugin contexts (e.g. tests): use the core
                // translator if present, otherwise pass the string through.
                $__ = function ($s) {
                    return function_exists('__') ? __($s) : $s;
                };
            }
        }
        return $__($msgid);
    }

    /* -- formatting ------------------------------------------------------- */

    /**
     * Format a number of seconds as HH:MM (optionally HH:MM:SS).
     */
    static function formatDuration($seconds, $withSeconds = false) {
        $seconds = (int) $seconds;
        $sign = '';
        if ($seconds < 0) { $sign = '-'; $seconds = -$seconds; }

        $hrs = intdiv($seconds, 3600);
        $min = intdiv($seconds - $hrs * 3600, 60);
        $sec = $seconds - $hrs * 3600 - $min * 60;

        if (!$withSeconds && $sec > 30) {
            $min++;
            if ($min >= 60) { $min -= 60; $hrs++; }
        }

        return $withSeconds
            ? sprintf('%s%02d:%02d:%02d', $sign, $hrs, $min, $sec)
            : sprintf('%s%02d:%02d', $sign, $hrs, $min);
    }

    /**
     * Format a monetary amount using the plugin configuration.
     */
    static function formatMoney($amount, $config = null) {
        $amount = (float) $amount;
        $symbol   = $config ? ($config->get('currency_symbol') ?: '€') : '€';
        $position = $config ? ($config->get('currency_position') ?: 'after') : 'after';
        $dec      = $config ? ($config->get('decimal_sep')  ?: ',') : ',';
        $thou     = $config ? ($config->get('thousand_sep') ?: '.') : '.';

        $formatted = number_format($amount, 2, $dec, $thou);
        return $position === 'before'
            ? $symbol.' '.$formatted
            : $formatted.' '.$symbol;
    }

    /* -- rounding --------------------------------------------------------- */

    /**
     * Round a duration (seconds) up to the configured billing increment.
     * increment = number of minutes; 0 disables rounding.
     */
    /**
     * Billable seconds = raw time x factor(%). Non-billable types yield 0.
     * This is the "Zeit abrechenbar" that doubles at factor 200, etc.
     */
    static function billableSeconds($seconds, $type) {
        $seconds = (int) $seconds;
        if ($type && !$type->isBillable()) return 0;
        $factor = $type ? $type->getFactor() : 100;
        return (int) round($seconds * $factor / 100.0);
    }

    /**
     * Values available to the configurable summary/PDF texts.
     * Notation follows osTicket: %{report.count} etc. The short form
     * %{count} and the legacy {count} keep working.
     */
    static function reportValues($rows, $config, $extra = array()) {
        $secs = 0; $bill = 0; $amount = 0.0; $on = 0; $off = 0; $trips = 0; $travel = 0.0;
        foreach ($rows as $r) {
            $secs   += (int) $r['seconds'];
            $bill   += (int) ($r['billable_seconds'] ?? 0);
            $amount += (float) $r['amount'];
            $trips  += (int) ($r['trips'] ?? 0);
            $travel += (float) ($r['travel'] ?? 0);
            if (!empty($r['onsite'])) $on++; else $off++;
        }
        $taxRate = $config ? (float) $config->get('tax_rate') : 0.0;
        $tax     = round($amount * $taxRate / 100.0, 2);
        $total   = round($amount + $tax, 2);
        $vals = array_merge(array(
            'count'    => count($rows),
            'onsite'   => $on,
            'office'   => $off,
            'trips'    => $trips,
            'travel'   => self::formatMoney($travel, $config),
            'time'     => self::formatDuration($secs),
            'billable' => self::formatDuration($bill),
            // 'subtotal'/'tax'/'total' - the same Subtotal/Tax(x%)/Total
            // breakdown shown on the invoice page, available as placeholders
            // for the table summary and PDF text fields.
            'subtotal' => self::formatMoney($amount, $config),
            'tax_rate' => rtrim(rtrim(number_format($taxRate, 2), '0'), '.'),
            'tax'      => self::formatMoney($tax, $config),
            'total'    => self::formatMoney($total, $config),
            'date'     => date('Y-m-d'),
            'year'     => date('Y'),
            'month'    => date('m'),
            'day'      => date('d'),
            'org'      => '',
            'from'     => '',
            'to'       => '',
        ), $extra);

        // Dates are stored/passed as ISO ('Y-m-d'); render them in the
        // helpdesk's format (German by default) for EVERY placeholder user,
        // so no view can print a raw ISO date any more.
        foreach (array('date', 'from', 'to') as $k)
            if (!empty($vals[$k]))
                $vals[$k] = self::formatDate($vals[$k]);
        return $vals;
    }

    /** Replace %{report.x} / %{x} / {x} in a text with the report values. */
    static function applyTokens($text, array $vals) {
        $text = (string) $text;
        if ($text === '') return '';
        $map = array();
        foreach ($vals as $k => $v) {
            $map['%{report.'.$k.'}'] = $v;
            $map['%{'.$k.'}']        = $v;
            $map['{'.$k.'}']         = $v;
        }
        return strtr($text, $map);
    }

    /** Default templates for the summary row under every table. */
    static function summaryDefaults($__) {
        return array(
            'left'  => $__('Total tickets: %{report.count}, on-site: %{report.onsite}, office: %{report.office}'),
            'right' => $__('Billable time: %{report.billable}'),
        );
    }

    /** Resolved summary texts (left/right) for the grey row under a table. */
    /**
     * Totals block shown under every report table. Adapts to the billing mode:
     * money mode gives the invoice breakdown (subtotal / tax / total), time
     * mode gives the durations instead. Returns a list of label => value pairs
     * so every view (web, CSV, PDF) renders the same figures.
     */
    /**
     * Label for a time type: appends the factor unless it is the neutral
     * 100 %. Without this a report showing "billable 10:30" next to a total of
     * "9:00" looks like an error to the customer - the factor is what explains
     * the difference.
     */
    static function typeLabel($name, $factor) {
        $name   = (string) $name;
        $factor = (int) $factor;
        if ($name === '' || $factor === 100 || $factor <= 0)
            return $name;
        return $name.' ('.$factor.' %)';
    }

    /**
     * Seconds gained purely through factors above 100 %. Reported as a line of
     * its own under the total time - a full per-type breakdown proved to be
     * more detail than a customer-facing report should carry.
     */
    static function surchargeSeconds($rows) {
        $extra = 0;
        foreach ($rows as $r) {
            $factor = (int) ($r['factor'] ?? 100);
            if ($factor === 100 || $factor <= 0)
                continue;
            $extra += ((int) ($r['billable_seconds'] ?? 0)) - ((int) $r['seconds']);
        }
        return max(0, $extra);
    }

    /**
     * Money value of the surcharges: the difference between what the recorded
     * time would cost at the neutral factor and what is actually invoiced.
     */
    static function surchargeAmount($rows) {
        $extra = 0.0;
        foreach ($rows as $r) {
            $factor = (int) ($r['factor'] ?? 100);
            if ($factor === 100 || $factor <= 0)
                continue;
            $rate = (float) ($r['rate'] ?? 0);
            $base = ((int) $r['seconds']) / 3600.0 * $rate;
            $extra += ((float) $r['amount']) - $base;
        }
        return max(0.0, round($extra, 2));
    }

    static function totalsBlock($rows, $config, $__) {
        // Can be switched off in the design settings (on by default).
        if ($config && !$config->get('show_totals', true))
            return array();
        $secs = 0; $bill = 0; $amount = 0.0; $trips = 0; $travelSum = 0.0;
        foreach ($rows as $r) {
            $secs   += (int) $r['seconds'];
            $bill   += (int) ($r['billable_seconds'] ?? 0);
            $amount += (float) $r['amount'];
            $trips  += (int) ($r['trips'] ?? 0);
            $travelSum += (float) ($r['travel'] ?? 0);
        }
        $timeMode = ($config && $config->get('billing_mode') === 'time');
        if ($timeMode) {
            $out = array($__('Total time') => self::formatDuration($secs));
            // Surcharges from non-neutral factors, listed directly under the
            // total so the jump to the billable time is obvious.
            $extra = self::surchargeSeconds($rows);
            if ($extra > 0)
                $out[$__('Surcharges for special hours')] = '+'.self::formatDuration($extra);
            // trips listed BEFORE the billable time so the latter stays the
            // highlighted last entry of the block
            if ($trips > 0)
                $out[$__('Trips')] = (string) $trips;
            $out[$__('Billable time')] = self::formatDuration($bill);
            return $out;
        }
        $taxRate = $config ? (float) $config->get('tax_rate') : 0.0;
        $tax     = round($amount * $taxRate / 100.0, 2);
        $out     = array();
        // Hourly-rate mode explains the surcharge in MONEY, not in time: base
        // amount for the recorded hours, the surcharge on top, then the sum.
        // Travel is broken out as its own cost line so the customer can see
        // WHY the invoice is higher (trips x the type's flat fee).
        $extraAmt = self::surchargeAmount($rows);
        if ($extraAmt > 0 || $travelSum > 0) {
            $out[$__('Base amount')] = self::formatMoney($amount - $extraAmt - $travelSum, $config);
            if ($extraAmt > 0)
                $out[$__('Surcharges for special hours')] = '+'.self::formatMoney($extraAmt, $config);
            if ($travelSum > 0)
                $out[sprintf($__('Travel charges (%d)'), $trips)] = '+'.self::formatMoney($travelSum, $config);
        }
        $out[$__('Subtotal')] = self::formatMoney($amount, $config);
        if ($taxRate > 0) {
            $label = sprintf($__('Tax (%s%%)'),
                rtrim(rtrim(number_format($taxRate, 2), '0'), '.'));
            $out[$label] = self::formatMoney($tax, $config);
        }
        $out[$__('Total')] = self::formatMoney($amount + $tax, $config);
        return $out;
    }

    static function summaryText($rows, $config, $__, $extra = array()) {
        // The two config fields carry their own default text (with
        // placeholders). If a field is left EMPTY the corresponding side prints
        // nothing - we no longer inject a hard-coded fallback here.
        $l = $config ? trim((string) $config->get('table_footer_left'))  : '';
        $r = $config ? trim((string) $config->get('table_footer_right')) : '';
        $vals = self::reportValues($rows, $config, $extra);
        return array(
            'left'  => $l === '' ? '' : self::applyTokens($l, $vals),
            'right' => $r === '' ? '' : self::applyTokens($r, $vals),
        );
    }

    /** Count on-site vs office rows for the export/table summary. */
    static function onsiteSummary($rows) {
        $on = 0; $off = 0;
        foreach ($rows as $r) {
            if (!empty($r['onsite'])) $on++; else $off++;
        }
        return array('onsite' => $on, 'office' => $off, 'total' => $on + $off);
    }

    /**
     * Turn raw seconds into the billed seconds, in this order:
     *   1. Minimum charge floor ("Bagatellgrenze"): below X min -> 0.
     *   2. Rounding: round up to the increment, but only once the entry
     *      reaches the rounding threshold (shorter entries stay exact).
     *   3. Minimum billable time: anything still > 0 is lifted to at least Y.
     */
    static function applyIncrement($seconds, $config = null) {
        $seconds = (int) $seconds;
        if ($seconds <= 0)
            return $seconds;

        // 1) do-not-bill-under: entries shorter than this become 0
        $dropBelow = $config ? (int) $config->get('drop_below') : 0;
        if ($dropBelow > 0 && $seconds < $dropBelow * 60)
            return 0;

        // 2) round up to the configured increment block
        $inc = $config ? (int) $config->get('round_increment') : 0;
        if ($inc > 0) {
            $step = $inc * 60;
            $seconds = (int) (ceil($seconds / $step) * $step);
        }
        return $seconds;
    }

    /* -- per-object detail ------------------------------------------------ */

    /**
     * Return the individual time entries for one ticket / task as an array
     * of associative rows, enriched with poster and staff name.
     *
     * @param int    $object_id
     * @param string $object_type  'T' (ticket) or 'A' (task)
     */
    static function getEntriesForObject($object_id, $object_type) {
        $object_id = (int) $object_id;
        if (!$object_id || !in_array($object_type, array('T', 'A')) || !self::timesheetAvailable())
            return array();

        $te    = TABLE_PREFIX.'thread_entry';
        $staff = TABLE_PREFIX.'staff';

        // Use the whole-ticket type override (billing_ticket_type) instead of
        // each row's raw time_type_id, so this list shows the SAME type as the
        // invoice summary and the ticket detail row. Otherwise a row that Time
        // Recording booked with the default type would display "Normal" here
        // while every other view shows the chosen type.
        $bttTbl  = TABLE_PREFIX.'billing_ticket_type';
        $useBtt  = ($object_type === 'T') && self::tableExists($bttTbl);
        $typeSel = $useBtt ? 'COALESCE(btt.time_type_id, ts.time_type_id)' : 'ts.time_type_id';
        $bttJoin = $useBtt ? 'LEFT JOIN `'.$bttTbl.'` btt ON (btt.ticket_id = ts.object_id) ' : '';

        $sql = 'SELECT ts.id, '.$typeSel.' AS time_type_id, ROUND(ts.time/60)*60 AS secs, ts.settled, '
             . '       ts.staff_id, ts.created, ts.thread_entry_id, '
             . '       te.poster, te.title, te.type AS entry_type, '
             . "       TRIM(CONCAT(COALESCE(s.firstname,''),' ',COALESCE(s.lastname,''))) AS staff_name "
             . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '
             . $bttJoin
             . 'LEFT JOIN `'.$te.'` te ON te.id = ts.thread_entry_id '
             . 'LEFT JOIN `'.$staff.'` s ON s.staff_id = ts.staff_id '
             . 'WHERE ts.object_id = '.db_input($object_id).' '
             . 'AND ts.object_type = '.db_input($object_type).' '
             . 'ORDER BY ts.created ASC, ts.id ASC';

        $rows = array();
        if (($res = db_query($sql))) {
            while (($r = db_fetch_array($res)))
                $rows[] = $r;
        }
        return $rows;
    }

    /**
     * Aggregate an object's time by time type and compute an invoice.
     *
     * @param bool $onlyUnsettled  only include not-yet-billed entries
     * @return array {
     *     lines   => list of [type_id, name, seconds, billed_seconds, hours,
     *                          rate, amount, billable, settled_all],
     *     total_seconds, billable_seconds, subtotal, tax_rate, tax, total
     * }
     */
    /**
     * An explicit per-ticket assignment (billing_ticket_type, set via the
     * ticket picker) always wins over each timesheet row's own time_type_id.
     * Returns array($selectExpr, $joinSql) to splice into a query using alias
     * "ts" for the timesheet and object_id as the ticket id.
     */
    static function ticketTypeOverride($tsAlias = 'ts') {
        $btt = TABLE_PREFIX.'billing_ticket_type';
        if (!self::tableExists($btt))
            return array($tsAlias.'.time_type_id', '');
        return array(
            'COALESCE(btt.time_type_id, '.$tsAlias.'.time_type_id)',
            'LEFT JOIN `'.$btt.'` btt ON (btt.ticket_id = '.$tsAlias.'.object_id) ',
        );
    }

    static function computeInvoice($object_id, $object_type, $config, $onlyUnsettled = false) {
        $object_id = (int) $object_id;
        $result = array(
            'lines' => array(), 'total_seconds' => 0, 'billable_seconds' => 0,
            'subtotal' => 0.0, 'tax_rate' => 0.0, 'tax' => 0.0, 'total' => 0.0,
            'is_goodwill' => false, 'trips' => 0, 'travel_fee' => 0.0, 'travel_amount' => 0.0,
        );
        if (!$object_id || !in_array($object_type, array('T', 'A')) || !self::timesheetAvailable())
            return $result;

        // "Kulanz": the work is still recorded and shown (rate/time stay
        // visible for transparency and reporting), but nothing is invoiced -
        // every line's amount is zeroed and the billable total collapses to 0.
        $isGoodwill = false;
        if ($object_type === 'T') {
            $gwRes = db_query('SELECT is_goodwill FROM `'.TABLE_PREFIX.'billing_ticket_type` '
                             . 'WHERE ticket_id = '.db_input($object_id), false);
            if ($gwRes && ($row = db_fetch_row($gwRes)))
                $isGoodwill = ((int) $row[0]) === 1;
        }
        $result['is_goodwill'] = $isGoodwill;

        $where = 'ts.object_id = '.db_input($object_id).' '
               . 'AND ts.object_type = '.db_input($object_type).' ';
        if ($onlyUnsettled)
            $where .= "AND ts.settled = '0' ";

        list($typeSel, $bttJoin) = ($object_type === 'T')
            ? self::ticketTypeOverride('ts') : array('ts.time_type_id', '');
        $sql = 'SELECT '.$typeSel.' AS time_type_id, SUM(ROUND(ts.time/60)*60) AS secs, '
             . "       MIN(ts.settled) AS min_settled, COUNT(*) AS n "
             . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '.$bttJoin
             . 'WHERE '.$where
             . 'GROUP BY '.$typeSel.' '
             . 'ORDER BY time_type_id ASC';

        $types    = BillingTimeType::getAll();
        $defRate  = $config ? (float) $config->get('default_rate') : 0.0;
        $taxRate  = $config ? (float) $config->get('tax_rate') : 0.0;

        $subtotal = 0.0;
        if (($res = db_query($sql))) {
            while (($r = db_fetch_array($res))) {
                $tid   = (int) $r['time_type_id'];
                $secs  = (int) $r['secs'];
                $type  = isset($types[$tid]) ? $types[$tid] : null;

                $name     = $type ? $type->getName() : sprintf(self::__t('Type %d'), $tid);
                $billable = $type ? $type->isBillable() : true;
                $rate     = $type ? $type->getHourlyRate() : $defRate;
                if ($rate <= 0) $rate = $defRate;

                $billedSecs = self::applyIncrement(self::billableSeconds($secs, $type), $config);
                $bsec       = $isGoodwill ? 0 : $billedSecs;
                $hours      = $billedSecs / 3600.0;
                $amount     = $isGoodwill ? 0.0 : round($hours * $rate, 2);

                $result['lines'][] = array(
                    'type_id'        => $tid,
                    'name'           => $name,
                    'factor'         => $type ? (int) $type->getFactor() : 100,
                    'seconds'        => $secs,
                    'billed_seconds' => $billedSecs,
                    'hours'          => $hours,
                    'rate'           => $rate,
                    'amount'         => $amount,
                    'billable'       => $billable,
                    'is_goodwill'    => $isGoodwill,
                    'settled_all'    => ($r['min_settled'] === '1'),
                );
                // flat travel fee of the ticket's time type (the type that
                // actually carries a fee wins when bookings are mixed)
                if ($type && $type->getTravelFee() > 0 && $result['travel_fee'] <= 0)
                    $result['travel_fee'] = (float) $type->getTravelFee();
                $result['total_seconds'] += $secs;
                $result['billable_seconds'] += $bsec;
                $subtotal += $amount;
            }
        }

        // Trips ("Anfahrten"): the per-ticket count times the flat fee of the
        // ticket's time type, added on top of the time-based amount so the
        // single-ticket invoice matches the reports.
        if ($object_type === 'T' && self::tableExists(TABLE_PREFIX.'billing_ticket_type')) {
            $tr = db_query('SELECT trips FROM `'.TABLE_PREFIX.'billing_ticket_type`'
                         . ' WHERE ticket_id = '.$object_id, false);
            if ($tr && ($trow = db_fetch_row($tr)))
                $result['trips'] = (int) $trow[0];
        }
        $result['travel_amount'] = ($isGoodwill || $result['trips'] <= 0)
            ? 0.0
            : round($result['trips'] * $result['travel_fee'], 2);
        $subtotal += $result['travel_amount'];

        $result['subtotal'] = round($subtotal, 2);
        $result['tax_rate'] = $taxRate;
        $result['tax']      = round($subtotal * $taxRate / 100.0, 2);
        $result['total']    = round($result['subtotal'] + $result['tax'], 2);

        // Surcharges caused by factors above 100 %, both as time and as money,
        // so the single-ticket invoice can explain its figures the same way
        // the reports do.
        $sSecs = 0; $sAmount = 0.0;
        foreach ($result['lines'] as $ln) {
            $factor = (int) ($ln['factor'] ?? 100);
            if ($factor === 100 || $factor <= 0)
                continue;
            $sSecs   += ((int) $ln['billed_seconds']) - ((int) $ln['seconds']);
            $base     = ((int) $ln['seconds']) / 3600.0 * ((float) $ln['rate']);
            $sAmount += ((float) $ln['amount']) - $base;
        }
        $result['surcharge_seconds'] = max(0, $sSecs);
        $result['surcharge_amount']  = max(0.0, round($sAmount, 2));
        return $result;
    }

    /* -- settling (writing back) ------------------------------------------ */

    /**
     * Mark specific timesheet rows as settled / unsettled.
     *
     * @param int[]  $ids
     * @param bool   $settled
     */
    static function setEntriesSettled(array $ids, $settled = true) {
        $ids = array_values(array_filter(array_map('intval', $ids)));
        if (!$ids || !self::timesheetAvailable())
            return false;
        $flag = $settled ? '1' : '0';
        $sql = 'UPDATE `'.BILLING_TIMESHEET_TABLE.'` '
             . 'SET settled = '.db_input($flag).', updated = NOW() '
             . 'WHERE id IN ('.implode(',', $ids).')';
        return db_query($sql) ? db_affected_rows() : false;
    }

    /**
     * Mark every (optionally: only billable) entry of an object as settled.
     */
    static function settleObject($object_id, $object_type, $onlyBillable = true, $settled = true) {
        $object_id = (int) $object_id;
        if (!$object_id || !in_array($object_type, array('T', 'A')) || !self::timesheetAvailable())
            return false;

        $flag = $settled ? '1' : '0';
        $sql = 'UPDATE `'.BILLING_TIMESHEET_TABLE.'` '
             . 'SET settled = '.db_input($flag).', updated = NOW() '
             . 'WHERE object_id = '.db_input($object_id).' '
             . 'AND object_type = '.db_input($object_type).' ';

        if ($onlyBillable) {
            // limit to time types that are flagged billable
            $billableIds = array();
            foreach (BillingTimeType::getAll() as $tt) {
                if ($tt->isBillable())
                    $billableIds[] = $tt->getId();
            }
            if (!$billableIds)
                return 0;
            $sql .= 'AND time_type_id IN ('.implode(',', array_map('intval', $billableIds)).') ';
        }
        return db_query($sql) ? db_affected_rows() : false;
    }

    /* -- organisation billing (tickets only) ------------------------------ */

    /**
     * Collect the billable time of every ticket belonging to an organization
     * within a date range (by time-entry date).
     *
     * @return array list of tickets, each:
     *   [ticket_id, number, subject, lines(by type), subtotal, seconds]
     *   plus an aggregated 'org_totals' summary.
     */
    /**
     * List every ticket and task that still has unsettled ('0') time entries,
     * newest activity first — the "open items" worklist. No filters needed:
     * this is what still has to be billed.
     *
     * Returns rows: object_type ('T'|'A'), object_id, number, subject,
     * org (name or ''), seconds, amount (billable lines only), last (datetime).
     */
    static function getOpenItems($config, $limit = 100) {
        $items = array();
        if (!self::timesheetAvailable())
            return $items;

        $types   = BillingTimeType::getAll();
        $defRate = $config ? (float) $config->get('default_rate') : 0.0;

        $ticket = TABLE_PREFIX.'ticket';
        $user   = TABLE_PREFIX.'user';
        $orgTbl = TABLE_PREFIX.'organization';
        $task   = TABLE_PREFIX.'task';
        $cdata  = TABLE_PREFIX.'ticket__cdata';

        $hasCdata   = self::tableExists($cdata);
        $subjectSel = $hasCdata ? 'c.subject AS subject' : "'' AS subject";
        $cdataJoin  = $hasCdata ? 'LEFT JOIN `'.$cdata.'` c ON (c.ticket_id = t.ticket_id) ' : '';
        $hasTask    = self::tableExists($task);

        // Tickets with unsettled entries (grouped per type for the money math)
        list($typeSel, $bttJoin) = self::ticketTypeOverride('ts');
        $notBilled = $bttJoin !== '' ? 'AND (btt.billed IS NULL OR btt.billed = 0) ' : '';
        $sql = 'SELECT ts.object_id, '.$typeSel.' AS time_type_id, SUM(ROUND(ts.time/60)*60) AS secs, '
             . '       MAX(ts.created) AS last, t.number, '.$subjectSel.', '
             . '       COALESCE(o.name, \'\') AS org, '
             . ($bttJoin !== '' ? 'MAX(COALESCE(btt.is_goodwill,0)) AS is_goodwill, MAX(COALESCE(btt.trips,0)) AS trips '
                                 : '0 AS is_goodwill, 0 AS trips ')
             . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '
             . 'JOIN `'.$ticket.'` t ON (t.ticket_id = ts.object_id) '
             . 'LEFT JOIN `'.$user.'` u ON (u.id = t.user_id) '
             . 'LEFT JOIN `'.$orgTbl.'` o ON (o.id = u.org_id) '
             . $cdataJoin.$bttJoin
             . "WHERE ts.object_type = 'T' AND ts.settled = '0'".self::statusWhere($config).' '
             . $notBilled
             . 'GROUP BY ts.object_id, '.$typeSel;
        self::collectOpenRows($items, db_query($sql), 'T', $types, $defRate, $config);

        // Tasks with unsettled entries
        if ($hasTask) {
            $sql = 'SELECT ts.object_id, ts.time_type_id, SUM(ROUND(ts.time/60)*60) AS secs, '
                 . '       MAX(ts.created) AS last, tk.number, '
                 . "       '' AS subject, '' AS org "
                 . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '
                 . 'JOIN `'.$task.'` tk ON (tk.id = ts.object_id) '
                 . "WHERE ts.object_type = 'A' AND ts.settled = '0' "
                 . 'GROUP BY ts.object_id, ts.time_type_id';
            self::collectOpenRows($items, db_query($sql), 'A', $types, $defRate, $config);
        }

        // newest activity first, cap the list
        usort($items, function ($a, $b) { return strcmp($b['last'], $a['last']); });
        return array_slice(array_values($items), 0, max(1, (int) $limit));
    }

    /**
     * Fold grouped type rows into per-object open items (shared by both
     * queries above).
     */
    private static function collectOpenRows(&$items, $res, $otype, $types, $defRate, $config) {
        if (!$res)
            return;
        while (($r = db_fetch_array($res))) {
            $key = $otype.':'.(int) $r['object_id'];
            if (!isset($items[$key])) {
                $items[$key] = array(
                    'object_type' => $otype,
                    'object_id'   => (int) $r['object_id'],
                    'number'      => $r['number'],
                    'subject'     => (string) $r['subject'],
                    'org'         => (string) $r['org'],
                    'seconds'          => 0,
                    'billable_seconds' => 0,
                    'onsite'      => 0,
                    'amount'      => 0.0,
                    'last'        => (string) $r['last'],
                    'billed'      => false,
                    'is_goodwill' => false,
                    // Open Items rows previously carried no type name at all,
                    // so a "Time type" column stayed blank in this module.
                    'type_name'   => '',
                    'billable'    => true,
                );
            }
            if (!empty($r['is_goodwill'])) $items[$key]['is_goodwill'] = true;
            $tid  = (int) $r['time_type_id'];
            $secs = (int) $r['secs'];
            $type = isset($types[$tid]) ? $types[$tid] : null;
            $billable = $type ? $type->isBillable() : true;
            $rate = $type ? $type->getHourlyRate() : $defRate;
            if ($rate <= 0) $rate = $defRate;

            $name = $type ? $type->getName() : sprintf(self::__t('Type %d'), $tid);
            if ($items[$key]['type_name'] === '')
                $items[$key]['type_name'] = $name;
            elseif (strpos($items[$key]['type_name'], $name) === false)
                $items[$key]['type_name'] .= ', '.$name;
            $items[$key]['billable'] = $billable;
            // Factor and rate are what surchargeSeconds()/surchargeAmount()
            // need. Without them this module reported no surcharges at all,
            // even though the report and the invoice page did.
            $items[$key]['factor'] = $type ? (int) $type->getFactor() : 100;
            $items[$key]['rate']   = $billable ? $rate : 0.0;

            $bsec = self::applyIncrement(self::billableSeconds($secs, $type), $config);
            $isGw = $items[$key]['is_goodwill'];
            $items[$key]['seconds'] += $secs;
            $items[$key]['billable_seconds'] += $isGw ? 0 : $bsec;
            if ((int) ($r['trips'] ?? 0) > 0) $items[$key]['onsite'] = 1;
            if ($billable && !$isGw)
                $items[$key]['amount'] += round(($bsec / 3600.0) * $rate, 2);
            if ((string) $r['last'] > $items[$key]['last'])
                $items[$key]['last'] = (string) $r['last'];
        }
        foreach ($items as &$it)
            $it['amount'] = round($it['amount'], 2);
        unset($it);
    }

    /**
     * Flat, entry-level report over the timesheet — the data source for the
     * customizable report table and its CSV/PDF exports.
     *
     * $filters keys (all optional): settled (''|'0'|'1'), otype (''|'T'|'A'),
     * org_id (int), time_type_id (int), date_from / date_to ('Y-m-d'),
     * sort (one of the whitelist below), dir ('asc'|'desc').
     *
     * Returns rows: id, created, object_type, object_id, number, subject,
     * org, agent, type_name, seconds, rate, amount, billable, settled.
     */
    /** All custom/dynamic ticket field columns from ost_ticket__cdata. */
    static function cdataColumns() {
        static $cols = null;
        if ($cols !== null) return $cols;
        $cols = array();
        $cdata = TABLE_PREFIX.'ticket__cdata';
        if (self::tableExists($cdata) && ($res = db_query('SHOW COLUMNS FROM `'.$cdata.'`', false)))
            while (($r = db_fetch_row($res)))
                if (!in_array($r[0], array('ticket_id', 'subject', 'time_total')))
                    $cols[] = $r[0];
        return $cols;
    }

    /** All ticket statuses (id => name), cached. */
    static function ticketStatuses() {
        static $st = null;
        if ($st !== null) return $st;
        $st = array();
        $tbl = TABLE_PREFIX.'ticket_status';
        if (self::tableExists($tbl) && ($res = db_query('SELECT id, name FROM `'.$tbl.'` ORDER BY sort, name', false)))
            while (($r = db_fetch_row($res)))
                $st[(int) $r[0]] = (string) $r[1];
        return $st;
    }

    /** WHERE fragment restricting tickets to the configured status. */
    private static function statusWhere($config) {
        $sf = $config ? (int) $config->get('status_filter') : 0;
        return $sf ? ' AND t.status_id = '.$sf : '';
    }

    /** Custom form fields that have ticket entries (id => label), cached. */
    static function formFields() {
        static $f = null;
        if ($f !== null) return $f;
        $f = array();
        $ff = TABLE_PREFIX.'form_field'; $fe = TABLE_PREFIX.'form_entry';
        $fv = TABLE_PREFIX.'form_entry_values';
        if (self::tableExists($ff) && self::tableExists($fe) && self::tableExists($fv)) {
            $sql = 'SELECT DISTINCT ff.id, ff.label FROM `'.$ff.'` ff '
                 . 'JOIN `'.$fv.'` fv ON (fv.field_id = ff.id) '
                 . 'JOIN `'.$fe.'` fe ON (fe.id = fv.entry_id) '
                 . "WHERE fe.object_type = 'T' AND ff.name <> 'subject' "
                 . 'ORDER BY ff.label LIMIT 60';
            if (($res = db_query($sql, false)))
                while (($r = db_fetch_row($res)))
                    $f[(int) $r[0]] = (string) $r[1];
        }
        return $f;
    }

    /** Decode a form_entry_values value (may be JSON like {"1":"Label"}). */
    static function formValue($v) {
        $v = (string) $v;
        if ($v !== '' && ($v[0] === '{' || $v[0] === '[')) {
            $d = json_decode($v, true);
            if (is_array($d)) return implode(', ', array_map('strval', array_values($d)));
        }
        return $v;
    }

    /**
     * Shared SQL builder for the "extra" ticket columns (native core fields,
     * cdata custom fields, and form-entry custom fields). Returns
     * array($select, $join). Assumes the ticket table is aliased `t` and the
     * user table `u` (the caller joins `u`). Used by both getEntriesReport()
     * and enrichRows() so the two column sets never drift apart.
     */
    private static function ticketExtraColumns() {
        $cdata = TABLE_PREFIX.'ticket__cdata';
        $dept  = TABLE_PREFIX.'department';
        $tstat = TABLE_PREFIX.'ticket_status';
        $team  = TABLE_PREFIX.'team';
        $staff = TABLE_PREFIX.'staff';

        $sel  = ', t.created AS core_tcreated, t.duedate AS core_due, t.lastupdate AS core_updated'
              . ", COALESCE(u.name,'') AS core_user";
        $join = '';

        if (self::tableExists($dept)) {
            $sel  .= ", COALESCE(dp.name,'') AS core_dept";
            $join .= 'LEFT JOIN `'.$dept.'` dp ON (dp.id = t.dept_id) ';
        } else $sel .= ", '' AS core_dept";
        if (self::tableExists($tstat)) {
            $sel  .= ", COALESCE(sx.name,'') AS core_status";
            $join .= 'LEFT JOIN `'.$tstat.'` sx ON (sx.id = t.status_id) ';
        } else $sel .= ", '' AS core_status";
        if (self::tableExists($team)) {
            $sel  .= ", COALESCE(tmx.name,'') AS core_team";
            $join .= 'LEFT JOIN `'.$team.'` tmx ON (tmx.team_id = t.team_id) ';
        } else $sel .= ", '' AS core_team";
        if (self::tableExists($staff)) {
            $sel  .= ", TRIM(CONCAT(COALESCE(sax.firstname,''),' ',COALESCE(sax.lastname,''))) AS core_assigned";
            $join .= 'LEFT JOIN `'.$staff.'` sax ON (sax.staff_id = t.staff_id) ';
        } else $sel .= ", '' AS core_assigned";

        if (self::tableExists($cdata)) {
            $join .= 'LEFT JOIN `'.$cdata.'` c ON (c.ticket_id = t.ticket_id) ';
            foreach (self::cdataColumns() as $cc)
                $sel .= ', c.`'.$cc.'` AS `cd_'.$cc.'`';
        }
        $fe = TABLE_PREFIX.'form_entry'; $fv = TABLE_PREFIX.'form_entry_values';
        foreach (self::formFields() as $fid => $flabel)
            $sel .= ', (SELECT fv2.value FROM `'.$fe.'` fe2 '
                  . 'JOIN `'.$fv.'` fv2 ON (fv2.entry_id = fe2.id) '
                  . "WHERE fe2.object_type = 'T' AND fe2.object_id = t.ticket_id "
                  . 'AND fv2.field_id = '.(int) $fid.' LIMIT 1) AS `ff_'.(int) $fid.'`';

        return array($sel, $join);
    }

    /**
     * Add the native ticket fields + custom/form fields to already-built rows
     * (used by the open-items and org tables, which have leaner queries).
     * One query for all ticket ids in $rows. Rows are keyed by object_type/id.
     */
    static function enrichRows(&$rows, $config) {
        if (!$rows) return;
        $ids = array();
        foreach ($rows as $r)
            if (($r['object_type'] ?? '') === 'T' && (int) $r['object_id'])
                $ids[(int) $r['object_id']] = true;
        if (!$ids) return;
        $idList = implode(',', array_map('intval', array_keys($ids)));

        $ticket = TABLE_PREFIX.'ticket';
        $user   = TABLE_PREFIX.'user';
        $staff  = TABLE_PREFIX.'staff';   // used by the booking-agent aggregate below

        list($extraSel, $extraJoin) = self::ticketExtraColumns();
        $sel  = 't.ticket_id AS _tid, t.closed AS closed'.$extraSel;
        $join = 'LEFT JOIN `'.$user.'` u ON (u.id = t.user_id) '.$extraJoin;

        $extra = array();
        $res = db_query('SELECT '.$sel.' FROM `'.$ticket.'` t '.$join.'WHERE t.ticket_id IN ('.$idList.')');
        if ($res)
            while (($r = db_fetch_array($res))) {
                $tid = (int) $r['_tid']; unset($r['_tid']);
                $extra[$tid] = $r;
            }

        // Booking agent(s) per ticket from the timesheet (aggregated rows have
        // no single agent; concatenate the distinct ones who logged time).
        $agents = array();
        if (self::tableExists($staff)) {
            $ts = BILLING_TIMESHEET_TABLE;
            $aq = 'SELECT ts.object_id AS oid, '
                . "GROUP_CONCAT(DISTINCT TRIM(CONCAT(COALESCE(s.firstname,''),' ',COALESCE(s.lastname,''))) "
                . "ORDER BY s.firstname SEPARATOR ', ') AS agents "
                . 'FROM `'.$ts.'` ts LEFT JOIN `'.$staff.'` s ON (s.staff_id = ts.staff_id) '
                . "WHERE ts.object_type = 'T' AND ts.object_id IN (".$idList.') '
                . 'GROUP BY ts.object_id';
            if (($ares = db_query($aq)))
                while (($ar = db_fetch_array($ares)))
                    $agents[(int) $ar['oid']] = trim((string) $ar['agents']);
        }
        foreach ($rows as &$row) {
            if (($row['object_type'] ?? '') !== 'T') continue;
            $tid = (int) $row['object_id'];
            if (isset($extra[$tid]))
                foreach ($extra[$tid] as $k => $v)
                    $row[$k] = (string) $v;
            if ((($row['agent'] ?? '') === '') && isset($agents[$tid]) && $agents[$tid] !== '')
                $row['agent'] = $agents[$tid];
        }
        unset($row);
    }

    /**
     * Parse a date coming from the native osTicket datepicker. The picker
     * writes whatever the site's date format is, so accept the common ones
     * (ISO, German, US) and always return ISO (Y-m-d) or '' when unusable.
     */
    /**
     * Render a stored date (Y-m-d[ H:i:s]) in the helpdesk's CONFIGURED date
     * format, so table/CSV/PDF values follow the same localization as the
     * rest of osTicket instead of showing raw ISO values. Date only - the
     * time part is intentionally dropped for billing views.
     */
    static function formatDate($value) {
        $value = trim((string) $value);
        if ($value === '') return '';
        if (class_exists('Format') && method_exists('Format', 'date')) {
            $out = Format::date($value);
            if ($out) return $out;
        }
        global $cfg;
        $fmt = ($cfg && method_exists($cfg, 'getDateFormat')) ? $cfg->getDateFormat() : '';
        $ts  = strtotime($value);
        // German day-first default when the helpdesk has no format configured
        return $ts ? date($fmt ?: 'd.m.Y', $ts) : substr($value, 0, 10);
    }

    /**
     * Same as formatDate() but keeps the time - used for "last modified"
     * stamps, which are stored as 'Y-m-d H:i:s' and were shown raw before.
     */
    static function formatDateTime($value) {
        $value = trim((string) $value);
        if ($value === '') return '';
        $ts = strtotime($value);
        if (!$ts) return $value;
        global $cfg;
        $d = ($cfg && method_exists($cfg, 'getDateFormat')) ? $cfg->getDateFormat() : '';
        $t = ($cfg && method_exists($cfg, 'getTimeFormat')) ? $cfg->getTimeFormat() : '';
        return date(($d ?: 'd.m.Y').' '.($t ?: 'H:i'), $ts);
    }

    static function parseDate($s) {
        $s = trim((string) $s);
        if ($s === '') return '';
        if (preg_match('/^(\d{4})-(\d{1,2})-(\d{1,2})$/', $s, $m))          // 2026-07-23
            return sprintf('%04d-%02d-%02d', $m[1], $m[2], $m[3]);
        if (preg_match('/^(\d{1,2})\.(\d{1,2})\.(\d{4})$/', $s, $m))        // 23.07.2026
            return sprintf('%04d-%02d-%02d', $m[3], $m[2], $m[1]);
        if (preg_match('#^(\d{1,2})/(\d{1,2})/(\d{4})$#', $s, $m))          // 07/23/2026
            return sprintf('%04d-%02d-%02d', $m[3], $m[1], $m[2]);
        $ts = strtotime($s);
        return $ts ? date('Y-m-d', $ts) : '';
    }

    /** Presets for the date quick-select (key => label), for the UI. */
    static function rangePresets($__) {
        return array(
            ''           => $__('Custom'),
            'this_month' => $__('Current month'),
            'last_month' => $__('Last month'),
            'this_week'  => $__('This week'),
            'last_week'  => $__('Last week'),
            'last7'      => $__('Last 7 days'),
            'last30'     => $__('Last 30 days'),
            'this_year'  => $__('This year'),
            'last_year'  => $__('Last year'),
            'all'        => $__('All'),
        );
    }

    /**
     * Resolve a preset key into array($from, $to) as ISO dates.
     * Returns null when the key is unknown (= keep the custom dates).
     */
    static function rangeDates($key) {
        $today = strtotime('today');
        switch ($key) {
            case 'this_month':
                return array(date('Y-m-01', $today), date('Y-m-t', $today));
            case 'last_month':
                $p = strtotime('first day of last month', $today);
                return array(date('Y-m-01', $p), date('Y-m-t', $p));
            case 'this_week':
                $mon = strtotime('monday this week', $today);
                return array(date('Y-m-d', $mon), date('Y-m-d', strtotime('+6 days', $mon)));
            case 'last_week':
                $mon = strtotime('monday last week', $today);
                return array(date('Y-m-d', $mon), date('Y-m-d', strtotime('+6 days', $mon)));
            case 'last7':
                return array(date('Y-m-d', strtotime('-6 days', $today)), date('Y-m-d', $today));
            case 'last30':
                return array(date('Y-m-d', strtotime('-29 days', $today)), date('Y-m-d', $today));
            case 'this_year':
                return array(date('Y-01-01', $today), date('Y-12-31', $today));
            case 'last_year':
                $y = (int) date('Y', $today) - 1;
                return array($y.'-01-01', $y.'-12-31');
            case 'all':
                return array('', '');
        }
        return null;
    }

    static function getEntriesReport(array $filters, $config) {
        $rows = array();
        if (!self::timesheetAvailable())
            return $rows;

        $types   = BillingTimeType::getAll();
        $defRate = $config ? (float) $config->get('default_rate') : 0.0;

        $ticket = TABLE_PREFIX.'ticket';
        $user   = TABLE_PREFIX.'user';
        $orgTbl = TABLE_PREFIX.'organization';
        $task   = TABLE_PREFIX.'task';
        $staff  = TABLE_PREFIX.'staff';
        $cdata  = TABLE_PREFIX.'ticket__cdata';

        $hasCdata   = self::tableExists($cdata);
        $subjectSel = $hasCdata ? 'c.subject AS subject' : "'' AS subject";
        // native core fields + cdata + form fields (shared with enrichRows);
        // the cdata join `c` is emitted here too, so we don't add it twice.
        list($extraSel, $coreJoin) = self::ticketExtraColumns();
        $subjectSel .= $extraSel;
        $cdataJoin  = '';
        $hasStaff   = self::tableExists($staff);
        $agentSel   = $hasStaff ? "TRIM(CONCAT(COALESCE(s.firstname,''),' ',COALESCE(s.lastname,''))) AS agent" : "'' AS agent";
        $staffJoin  = $hasStaff ? 'LEFT JOIN `'.$staff.'` s ON (s.staff_id = ts.staff_id) ' : '';

        // shared WHERE from the filters
        $where = '';
        if (isset($filters['settled']) && ($filters['settled'] === '0' || $filters['settled'] === '1'))
            $where .= " AND ts.settled = '".$filters['settled']."'";
        if (!empty($filters['time_type_id']))
            $where .= ' AND ts.time_type_id = '.(int) $filters['time_type_id'];
        if (!empty($filters['date_from']))
            $where .= ' AND ts.created >= '.db_input($filters['date_from'].' 00:00:00');
        if (!empty($filters['date_to']))
            $where .= ' AND ts.created <= '.db_input($filters['date_to'].' 23:59:59');

        $otype = isset($filters['otype']) ? $filters['otype'] : '';
        $orgId = !empty($filters['org_id']) ? (int) $filters['org_id'] : 0;

        // Tickets
        if ($otype === '' || $otype === 'T') {
            $orgWhere = $orgId ? ' AND u.org_id = '.$orgId : '';
            // An explicit per-ticket assignment (set via the ticket picker) wins
            // over each row's own time_type_id, so changing a ticket's type is
            // reflected immediately even for rows Time Recording booked with the
            // default type.
            $bttTbl  = TABLE_PREFIX.'billing_ticket_type';
            $hasBtt  = self::tableExists($bttTbl);
            $typeSel = $hasBtt ? 'COALESCE(btt.time_type_id, ts.time_type_id)' : 'ts.time_type_id';
            $bttJoin = $hasBtt ? 'LEFT JOIN `'.$bttTbl.'` btt ON (btt.ticket_id = ts.object_id) ' : '';
            $sql = 'SELECT ts.id, ts.created, ts.object_id, ts.time, '.$typeSel.' AS time_type_id, ts.settled, '
                 . '       t.number, t.closed AS closed, '.$subjectSel.', COALESCE(o.name, \'\') AS org, '.$agentSel.', '
                 . ($hasBtt ? 'COALESCE(btt.billed,0) AS billed, COALESCE(btt.is_goodwill,0) AS is_goodwill, COALESCE(btt.trips,0) AS trips '
                            : '0 AS billed, 0 AS is_goodwill, 0 AS trips ')
                 . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '
                 . 'JOIN `'.$ticket.'` t ON (t.ticket_id = ts.object_id) '
                 . 'LEFT JOIN `'.$user.'` u ON (u.id = t.user_id) '
                 . 'LEFT JOIN `'.$orgTbl.'` o ON (o.id = u.org_id) '
                 . $cdataJoin.$coreJoin.$staffJoin.$bttJoin
                 . "WHERE ts.object_type = 'T'".$where.$orgWhere.self::statusWhere($config);
            self::collectReportRows($rows, db_query($sql), 'T', $types, $defRate, $config);
        }

        // Tasks (an org filter implies tickets only — tasks carry no org)
        if (($otype === '' || $otype === 'A') && !$orgId && self::tableExists($task)) {
            $sql = 'SELECT ts.id, ts.created, ts.object_id, ts.time, ts.time_type_id, ts.settled, '
                 . "       tk.number, tk.closed AS closed, '' AS subject, '' AS org, ".$agentSel.' '
                 . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '
                 . 'JOIN `'.$task.'` tk ON (tk.id = ts.object_id) '
                 . $staffJoin
                 . "WHERE ts.object_type = 'A'".$where;
            self::collectReportRows($rows, db_query($sql), 'A', $types, $defRate, $config);
        }

        // collapse the per-object accumulator into final rows (one per ticket)
        self::finalizeReportRows($rows, $defRate, $config);

        // whitelist sorting (done in PHP; result sets are report-sized)
        $sortMap = array(
            'created' => 'created', 'closed' => 'closed', 'number' => 'number', 'subject' => 'subject',
            'org' => 'org', 'agent' => 'agent', 'type' => 'type_name',
            'time' => 'seconds', 'rate' => 'rate', 'amount' => 'amount',
            'settled' => 'settled',
        );
        $key = isset($filters['sort'], $sortMap[$filters['sort']])
             ? $sortMap[$filters['sort']] : 'created';
        $dir = (isset($filters['dir']) && strtolower($filters['dir']) === 'asc') ? 1 : -1;
        usort($rows, function ($a, $b) use ($key, $dir) {
            $x = $a[$key]; $y = $b[$key];
            if (is_numeric($x) && is_numeric($y))
                $c = ($x < $y) ? -1 : (($x > $y) ? 1 : 0);
            else
                $c = strcasecmp((string) $x, (string) $y);
            return $c * $dir;
        });
        return array_values($rows);
    }

    /**
     * Accumulate timesheet rows into ONE entry per object (ticket/task).
     * Because the rate/type is global per ticket, all of a ticket's bookings
     * share the same type; we sum their time and round once on the total.
     * Rows are keyed by "<otype>:<object_id>"; finalizeReportRows() turns the
     * accumulator into the final flat list.
     */
    private static function collectReportRows(&$rows, $res, $otype, $types, $defRate, $config) {
        if (!$res)
            return;
        while (($r = db_fetch_array($res))) {
            $oid  = (int) $r['object_id'];
            $key  = $otype.':'.$oid;
            $tid  = (int) $r['time_type_id'];
            $type = isset($types[$tid]) ? $types[$tid] : null;
            $secs = (int) (round(((int) $r['time']) / 60) * 60);  // bill in whole minutes

            if (!isset($rows[$key])) {
                $rows[$key] = array(
                    'object_type' => $otype,
                    'object_id'   => $oid,
                    'created'     => (string) $r['created'],
                    'closed'      => (string) ($r['closed'] ?? ''),
                    'number'      => (string) $r['number'],
                    'subject'     => (string) $r['subject'],
                    'org'         => (string) $r['org'],
                    'type'        => $type,
                    'type_name'   => $type ? $type->getName() : sprintf(self::__t('Type %d'), $tid),
                    '_secs'       => 0,
                    '_agents'     => array(),
                    '_allsettled' => true,
                    'settled'     => '1',
                    'billed'      => !empty($r['billed']),
                    'is_goodwill' => !empty($r['is_goodwill']),
                    'trips'       => (int) ($r['trips'] ?? 0),
                );
                foreach ($r as $rk => $rv)
                    if (strpos($rk, 'cd_') === 0 || strpos($rk, 'core_') === 0 || strpos($rk, 'ff_') === 0)
                        $rows[$key][$rk] = (string) $rv;
            }
            $rows[$key]['_secs'] += $secs;
            // keep the latest activity date as the row date
            if (($r['created'] ?? '') > $rows[$key]['created'])
                $rows[$key]['created'] = (string) $r['created'];
            $ag = trim((string) ($r['agent'] ?? ''));
            if ($ag !== '') $rows[$key]['_agents'][$ag] = true;
            if ((string) $r['settled'] !== '1') $rows[$key]['_allsettled'] = false;
        }
    }

    /** Turn the per-object accumulator into the final list of report rows. */
    private static function finalizeReportRows(&$rows, $defRate, $config) {
        foreach ($rows as $key => &$row) {
            if (!isset($row['_secs'])) continue;   // already final
            $type     = $row['type'];
            $billable = $type ? $type->isBillable() : true;
            $rate     = $type ? $type->getHourlyRate() : $defRate;
            if ($rate <= 0) $rate = $defRate;
            $secs = (int) $row['_secs'];
            $bsec = self::applyIncrement(self::billableSeconds($secs, $type), $config);

            $row['seconds']          = $secs;
            $row['billable_seconds'] = $bsec;
            $row['factor']           = $type ? $type->getFactor() : 100;
            // "on-site" is now derived from the trip count: >=1 trip = on-site,
            // 0 = remote ("Fernwartung"). The old per-type on-site flag is gone.
            $row['onsite']           = ((int) ($row['trips'] ?? 0) > 0) ? 1 : 0;
            $row['rate']             = $billable ? $rate : 0.0;
            // travel: trips (per ticket) x the type's travel flat fee
            $travelFee   = ($type && $type->getTravelFee() > 0) ? $type->getTravelFee() : 0.0;
            $trips       = isset($row['trips']) ? (int) $row['trips'] : 0;
            $travel      = (!empty($row['is_goodwill'])) ? 0.0 : round($trips * $travelFee, 2);
            $row['travel']           = $travel;
            $row['amount']           = round((($bsec / 3600.0) * $rate) + $travel, 2);
            $row['billable']         = $billable;
            $row['agent']            = implode(', ', array_keys($row['_agents']));
            $row['settled']          = $row['_allsettled'] ? '1' : '0';
            unset($row['_secs'], $row['_agents'], $row['_allsettled'], $row['type']);
        }
        unset($row);
        $rows = array_values($rows);
    }

    static function getOrgReport($orgId, $startDate, $endDate, $config) {
        $orgId  = (int) $orgId;
        $report = array('tickets' => array(), 'totals' => array(
            'seconds' => 0, 'subtotal' => 0.0, 'tax' => 0.0, 'total' => 0.0,
            'by_type' => array(),
        ));
        if (!$orgId || !self::timesheetAvailable())
            return $report;

        $ticket = TABLE_PREFIX.'ticket';
        $user   = TABLE_PREFIX.'user';
        $cdata  = TABLE_PREFIX.'ticket__cdata';

        // Date range: inclusive on both ends (whole days).
        $start = $startDate ? db_input($startDate.' 00:00:00') : "'1970-01-01 00:00:00'";
        $end   = $endDate   ? db_input($endDate.' 23:59:59')   : "'2999-12-31 23:59:59'";

        // The subject lives in the dynamic ticket cdata table; join it only
        // when it exists so the report never hard-fails on odd installs.
        $hasCdata   = self::tableExists($cdata);
        $subjectSel = $hasCdata ? 'c.subject AS subject' : "'' AS subject";
        $cdataJoin  = $hasCdata ? 'LEFT JOIN `'.$cdata.'` c ON (c.ticket_id = t.ticket_id) ' : '';

        // Honour the whole-ticket time-type override (billing_ticket_type) so
        // the organisation report shows the same type/rate as everywhere else.
        $bttTbl  = TABLE_PREFIX.'billing_ticket_type';
        $hasBtt  = self::tableExists($bttTbl);
        $typeSel = $hasBtt ? 'COALESCE(btt.time_type_id, ts.time_type_id)' : 'ts.time_type_id';
        $bttJoin = $hasBtt ? 'LEFT JOIN `'.$bttTbl.'` btt ON (btt.ticket_id = ts.object_id) ' : '';

        $sql = 'SELECT ts.object_id AS ticket_id, t.number, '.$subjectSel.', '
             . '       '.$typeSel.' AS time_type_id, SUM(ROUND(ts.time/60)*60) AS secs, '
             . ($hasBtt ? 'MAX(COALESCE(btt.billed,0)) AS billed, MAX(COALESCE(btt.is_goodwill,0)) AS is_goodwill, MAX(COALESCE(btt.trips,0)) AS trips '
                        : '0 AS billed, 0 AS is_goodwill, 0 AS trips ')
             . 'FROM `'.BILLING_TIMESHEET_TABLE.'` ts '
             . 'JOIN `'.$ticket.'` t ON (t.ticket_id = ts.object_id) '
             . 'JOIN `'.$user.'` u ON (u.id = t.user_id) '
             . $cdataJoin.$bttJoin
             . "WHERE ts.object_type = 'T' "
             . 'AND u.org_id = '.db_input($orgId).' '
             . 'AND ts.created BETWEEN '.$start.' AND '.$end.' '
             . trim(self::statusWhere($config)).' '
             . 'GROUP BY ts.object_id, '.$typeSel.' '
             . 'ORDER BY t.number ASC, '.$typeSel.' ASC';

        $types   = BillingTimeType::getAll();
        $defRate = $config ? (float) $config->get('default_rate') : 0.0;
        $taxRate = $config ? (float) $config->get('tax_rate') : 0.0;

        $tickets = array();
        if (($res = db_query($sql))) {
            while (($r = db_fetch_array($res))) {
                $tkId = (int) $r['ticket_id'];
                if (!isset($tickets[$tkId])) {
                    $tickets[$tkId] = array(
                        'ticket_id' => $tkId,
                        'number'    => $r['number'],
                        'subject'   => $r['subject'],
                        'lines'     => array(),
                        'seconds'   => 0,
                        'onsite'    => 0,
                        'subtotal'  => 0.0,
                        'type_name' => '',
                        // needed so the surcharge lines can be computed for
                        // the organisation report as well
                        'factor'      => 100,
                        'rate'        => 0.0,
                        'trips'       => 0,
                        'travel'      => 0.0,
                        '_travel_fee' => 0.0,
                        'billed'      => !empty($r['billed']),
                        'is_goodwill' => !empty($r['is_goodwill']),
                    );
                }
                $tid   = (int) $r['time_type_id'];
                $secs  = (int) $r['secs'];
                $type  = isset($types[$tid]) ? $types[$tid] : null;
                $name  = $type ? $type->getName() : sprintf(self::__t('Type %d'), $tid);
                $billable = $type ? $type->isBillable() : true;
                $rate  = $type ? $type->getHourlyRate() : $defRate;
                if ($rate <= 0) $rate = $defRate;

                $bsec       = self::applyIncrement(self::billableSeconds($secs, $type), $config);
                $isGw       = $tickets[$tkId]['is_goodwill'];
                $amount     = $isGw ? 0.0 : round(($bsec / 3600.0) * $rate, 2);

                $tickets[$tkId]['lines'][] = array(
                    'name' => $name, 'seconds' => $secs, 'rate' => $rate,
                    'amount' => $amount, 'billable' => $billable,
                );
                $tickets[$tkId]['seconds']  += $secs;
                // Ticket is billed as a whole; record its type name (join
                // distinct names on the rare chance of mixed legacy entries).
                $tickets[$tkId]['factor'] = $type ? (int) $type->getFactor() : 100;
                $tickets[$tkId]['rate']   = $rate;
                $tickets[$tkId]['trips']  = (int) ($r['trips'] ?? 0);
                // travel fee comes from the ticket's on-site time type; the
                // type that actually carries a fee wins if lines are mixed.
                if ($type && $type->getTravelFee() > 0)
                    $tickets[$tkId]['_travel_fee'] = $type->getTravelFee();
                if ($tickets[$tkId]['type_name'] === '')
                    $tickets[$tkId]['type_name'] = $name;
                elseif (strpos($tickets[$tkId]['type_name'], $name) === false)
                    $tickets[$tkId]['type_name'] .= ', '.$name;
                if (!isset($tickets[$tkId]['billable_seconds'])) $tickets[$tkId]['billable_seconds'] = 0;
                $tickets[$tkId]['billable_seconds'] += $isGw ? 0 : $bsec;
                $tickets[$tkId]['subtotal'] += $amount;

                // org totals
                $report['totals']['seconds'] += $secs;
                if (!isset($report['totals']['billable_seconds'])) $report['totals']['billable_seconds'] = 0;
                $report['totals']['billable_seconds'] += $bsec;
                $report['totals']['subtotal'] += $amount;
                if (!isset($report['totals']['by_type'][$name]))
                    $report['totals']['by_type'][$name] = array('seconds' => 0, 'amount' => 0.0);
                $report['totals']['by_type'][$name]['seconds'] += $secs;
                $report['totals']['by_type'][$name]['amount']  += $amount;
            }
        }

        $travelTotal = 0.0;
        foreach ($tickets as &$t) {
            $tf     = isset($t['_travel_fee']) ? (float) $t['_travel_fee'] : 0.0;
            $trips  = isset($t['trips']) ? (int) $t['trips'] : 0;
            // goodwill tickets are recorded but not invoiced -> no travel charge
            $travel = $t['is_goodwill'] ? 0.0 : round($trips * $tf, 2);
            $t['travel']   = $travel;
            $t['onsite']   = ($trips > 0) ? 1 : 0;   // >=1 trip = on-site
            $t['subtotal'] = round($t['subtotal'] + $travel, 2);
            $travelTotal  += $travel;
            unset($t['_travel_fee']);
        }
        unset($t);

        $report['tickets'] = array_values($tickets);
        $sub = $report['totals']['subtotal'] + $travelTotal;
        $report['totals']['trips']    = array_sum(array_map(function ($t) { return (int) $t['trips']; }, $report['tickets']));
        $report['totals']['travel']   = round($travelTotal, 2);
        $report['totals']['subtotal'] = round($sub, 2);
        $report['totals']['tax']      = round($sub * $taxRate / 100.0, 2);
        $report['totals']['total']    = round($sub + $report['totals']['tax'], 2);
        $report['totals']['tax_rate'] = $taxRate;
        return $report;
    }
}
