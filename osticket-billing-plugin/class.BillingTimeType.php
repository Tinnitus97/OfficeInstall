<?php
/*********************************************************************
    class.BillingTimeType.php

    Time-type catalogue for the Time Billing plugin.

    Each row maps a `time_type_id` (as stored in ost_timesheet by the Time
    Recording plugin) to a human-readable name and an hourly rate, and marks
    whether that type is billable. The default row (id = 1) matches the
    default time_type_id written by the Time Recording plugin.

    PHP 8.4 compatible.
**********************************************************************/

if (!defined('INCLUDE_DIR')) die('Access Denied');

if (!defined('BILLING_TIME_TYPE_TABLE'))
    define('BILLING_TIME_TYPE_TABLE', TABLE_PREFIX.'billing_time_type');

class BillingTimeType extends VerySimpleModel {
    static $meta = array(
        'table'    => BILLING_TIME_TYPE_TABLE,
        'pk'       => array('id'),
        'ordering' => array('sort', 'name'),
    );

    /* -- convenience accessors -------------------------------------------- */

    function getId()          { return (int) $this->get('id'); }
    function getName()        { return $this->get('name'); }
    function getHourlyRate()  { return (float) $this->get('hourly_rate'); }
    function isBillable()     { return (bool) $this->get('billable'); }
    function getFactor()      { $f = (int) $this->get('factor'); return $f > 0 ? $f : 100; }
    function isOnsite()       { return (bool) $this->get('onsite'); }
    function getTravelFee()   { return (float) $this->get('travel_fee'); }
    function isDefault()      { return (bool) $this->get('isdefault'); }
    function isActive()       { return (bool) $this->get('isactive'); }

    function setName($v)      { $this->set('name', (string) $v); }
    function setHourlyRate($v){ $this->set('hourly_rate', (float) $v); }
    function setBillable($v)  { $this->set('billable', $v ? 1 : 0); }
    function setFactor($v)    { $v = (int) $v; $this->set('factor', $v > 0 ? $v : 100); }
    function setOnsite($v)    { $this->set('onsite', $v ? 1 : 0); }
    function setTravelFee($v) { $this->set('travel_fee', (float) $v); }

    function save($refetch = false) {
        $now = date('Y-m-d H:i:s');
        if ($this->__new__)
            $this->set('created', $now);
        $this->set('updated', $now);
        return parent::save($refetch);
    }

    /* -- static helpers --------------------------------------------------- */

    /**
     * Return every active time type keyed by id (id => BillingTimeType).
     */
    static function getActiveList() {
        // built from the cached full list to avoid an extra query
        $list = array();
        foreach (self::getAll() as $id => $tt)
            if ($tt->get('isactive'))
                $list[$id] = $tt;
        return $list;
    }

    /**
     * Return the whole catalogue (including inactive) keyed by id.
     */
    protected static $_cache = null;
    static function getAll() {
        // cached per request: the report and several signal handlers call
        // this repeatedly, and each objects() call is a database round-trip.
        // Call flushCache() after inserts/updates so the next read is fresh.
        if (static::$_cache === null) {
            static::$_cache = array();
            foreach (static::objects() as $tt)
                static::$_cache[$tt->getId()] = $tt;
        }
        return static::$_cache;
    }

    /** Drop the per-request cache so the next getAll() re-reads the database. */
    static function flushCache() {
        static::$_cache = null;
    }



    /**
     * Look up a single type, tolerating unknown ids (returns null).
     */
    static function byId($id) {
        return static::lookup(array('id' => (int) $id));
    }

    /**
     * Create a new type.
     *
     * Values are written straight into the model instead of going through
     * set(): the ORM marks a field as changed with a LOOSE comparison
     * ($old != $value), and on a fresh record $old is null. Since null == 0
     * in PHP, every zero value - "billable" unchecked, "on-site" unchecked,
     * sort 0 - was dropped from the INSERT and the column default (1) won.
     * That is why a type created with "billable" cleared came back billable.
     */
    static function create($vars = array()) {
        $tt = new static();
        foreach ($vars as $field => $value) {
            $tt->dirty[$field] = null;   // always part of the INSERT
            $tt->ht[$field]    = $value;
        }
        $tt->__new__ = true;
        return $tt;
    }
}
