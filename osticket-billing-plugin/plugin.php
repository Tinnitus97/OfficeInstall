<?php
/*********************************************************************
    plugin.php

    Time & Billing for osTicket – plugin manifest.

    This plugin adds a "Billing" application to the staff control panel.
    It does NOT record time itself; instead it reads the time entries
    written by the "Time Recording" plugin (table `ost_timesheet`) and
    turns them into billing reports and invoices.

    Compatible with osTicket v1.18+ and PHP 8.4.

    Adapted and maintained by tinnitus-ost.

    Released under the GNU General Public License WITHOUT ANY WARRANTY.
    See LICENSE.TXT for details.
**********************************************************************/

define('PLUGIN_BILLING_LATEST_VERSION', '1.0.0');

// Register the translation domain "billing" so that $__() works in every file.
list($__, $_N) = Plugin::translate('billing');

return array(
    'id'          => 'osticket:billing',            // notrans – unique namespace
    'version'     => PLUGIN_BILLING_LATEST_VERSION,
    'ost_version' => '1.18',                          // Require osTicket v1.18+
    'name'        => /* trans */ $__('Time Billing'),
    'author'      => 'tinnitus-ost inspired by Strobe Technologies',
    'description' => /* trans */ $__('Creates billing reports and invoices from recorded ticket / task times.'),
    'url'         => 'https://osticket.com.de/products/extensions',
    'plugin'      => 'billing.php:BillingPlugin',
);
