<?php
/*********************************************************************
    config.php

    Settings of the Time Billing plugin.

    This file declares WHAT can be configured. How those settings are
    laid out on screen - tabs, cards, order, and the rules that hide a
    setting while it cannot apply - lives in class.BillingConfigUi.php.

      getOptions()  flat definitions; osTicket calls this on every
                    request just to learn the defaults, so it stays cheap.
      getFields()   the same definitions arranged for the settings screen;
                    only called when the form is actually rendered or saved.

    PHP 8.4 compatible.
**********************************************************************/

require_once(INCLUDE_DIR.'class.plugin.php');
require_once(INCLUDE_DIR.'class.forms.php');
require_once(__DIR__.'/class.Billing.php');
require_once(__DIR__.'/class.BillingConfigUi.php');

class BillingConfig extends PluginConfig {

    /**
     * Ready-made number formats. Selecting one writes the three separate
     * settings below it, so everything that reads currency_position /
     * decimal_sep / thousand_sep keeps working unchanged.
     */
    static $numberFormats = array(
        'de' => array('thousand_sep' => '.',  'decimal_sep' => ',', 'currency_position' => 'after'),
        'ch' => array('thousand_sep' => "'",  'decimal_sep' => '.', 'currency_position' => 'before'),
        'en' => array('thousand_sep' => ',',  'decimal_sep' => '.', 'currency_position' => 'before'),
        'fr' => array('thousand_sep' => ' ',  'decimal_sep' => ',', 'currency_position' => 'after'),
    );

    function translate() {
        return Plugin::translate('billing');
    }

    /** Active time types as id => label, for the default-type dropdown. */
    static function timeTypeChoices($__) {
        $out = array('' => $__('— none —'));
        if (class_exists('BillingTimeType')) {
            foreach (BillingTimeType::getActiveList() as $id => $tt) {
                $lbl = $tt->getName();
                $f = (int) $tt->getFactor();
                if ($f !== 100) $lbl .= ' ('.$f.'%)';
                $out[(int) $id] = $lbl;
            }
        }
        return $out;
    }

    /**
     * Which of the ready-made number formats matches what is stored right
     * now. Detecting instead of defaulting matters for existing installs:
     * a preset must never silently rewrite separators somebody set by hand.
     */
    function detectNumberFormat() {
        // osTicket calls getOptions() from the PluginConfig constructor, before
        // the decoded cache exists, so this can still see the raw stored value.
        // BillingChoiceField::key() normalises it either way.
        $cur = array(
            'thousand_sep'      => (string) $this->get('thousand_sep', '.'),
            'decimal_sep'       => (string) $this->get('decimal_sep', ','),
            'currency_position' => BillingChoiceField::key(
                                       $this->get('currency_position', 'after')),
        );
        foreach (self::$numberFormats as $key => $fmt)
            if ($fmt == $cur)
                return $key;
        return 'custom';
    }

    /* ------------------------------------------------------------------
       Definitions
       ------------------------------------------------------------------ */

    function getOptions() {
        list($__, $_N) = self::translate();
        return $this->fieldDefs($__);
    }

    function getFields() {
        list($__, $_N) = self::translate();
        return BillingConfigUi::assemble($this->fieldDefs($__), $__, $this->notices($__));
    }

    /** Warnings shown above the tab bar, or '' when everything is fine. */
    private function notices($__) {
        if (class_exists('Billing') && !Billing::timesheetAvailable())
            return '<div class="bx-notice"><i class="icon-warning-sign"></i><div>'
                .'<b>'.Format::htmlchars($__('Time Recording is not installed')).'</b>'
                .Format::htmlchars($__('This plugin does not record time itself - it bills the entries the Time Recording plugin writes to the table ost_timesheet. That table is missing, so every report will stay empty until Time Recording is installed and enabled.'))
                .'</div></div>';
        return '';
    }

    private function fieldDefs($__) {
        return array(

        /* ---- Billing / model ---------------------------------------- */

        'billing_mode' => new BillingChoiceField(array(
            'label'   => $__('Billing model'),
            'default' => 'money',
            'choices' => array(
                'money' => $__('Amounts — hourly rates, tax, totals'),
                'time'  => $__('Time only — hours, no amounts'),
            ),
            'hint' => $__('"Time only" removes every rate, amount and tax column from reports, exports and the ticket view.'),
        )),
        'default_rate' => new TextboxField(array(
            'label'     => $__('Default hourly rate'),
            'default'   => '0',
            'validator' => 'number',
            'hint'      => $__('Used for time types that carry no rate of their own.'),
            'configuration' => array('size' => 8, 'length' => 10, 'inputmode' => 'decimal'),
        )),
        'tax_rate' => new TextboxField(array(
            'label'     => $__('Tax rate (%)'),
            'default'   => '19',
            'validator' => 'number',
            'hint'      => $__('Added to the subtotal of every invoice. 0 leaves the tax line out.'),
            'configuration' => array('size' => 6, 'length' => 6, 'inputmode' => 'decimal'),
        )),
        'manage_types' => new BillingInlineField(array(
            'label' => $__('Time types & rates'),
            'hint'  => $__('Name, hourly rate, travel charge, factor and the billable flag per time type. Kept on its own page because these are records, not settings.'),
            'configuration' => array(
                'content' => sprintf(
                    '<a class="button" href="%sscp/dispatcher.php/billing/timetypes">'
                    .'<i class="icon-list"></i> %s</a>',
                    ROOT_PATH, Format::htmlchars($__('Manage time types'))),
            ),
        )),

        /* ---- Billing / rounding ------------------------------------- */

        'round_increment' => new BillingChoiceField(array(
            'label'   => $__('Round up to'),
            'default' => '0',
            'choices' => array(
                '0'  => $__('Exact — do not round'),
                '15' => $__('15 minutes'),
                '30' => $__('30 minutes'),
                '45' => $__('45 minutes'),
                '60' => $__('60 minutes'),
            ),
            'hint' => $__('The total of each time type is rounded up to this block before it is billed.'),
        )),
        'drop_below' => new TextboxField(array(
            'label'     => $__('Ignore entries shorter than'),
            'default'   => '0',
            'validator' => 'number',
            'hint'      => $__('In minutes. Shorter entries are billed as zero. Leave at 0 to bill everything.'),
            'configuration' => array('size' => 5, 'length' => 5, 'inputmode' => 'numeric'),
        )),

        /* ---- Billing / number format -------------------------------- */

        'currency_symbol' => new TextboxField(array(
            'label'   => $__('Currency symbol'),
            'default' => '€',
            'configuration' => array('size' => 6, 'length' => 8),
        )),
        'number_format' => new BillingChoiceField(array(
            'label'   => $__('Number format'),
            'default' => $this->detectNumberFormat(),
            'choices' => array(
                'de'     => $__('1.234,56 € — German'),
                'ch'     => $__("€ 1'234.56 — Swiss"),
                'en'     => $__('€ 1,234.56 — English'),
                'fr'     => $__('1 234,56 € — French'),
                'custom' => $__('Custom…'),
            ),
            'hint' => $__('Picking a format sets the separators and the position of the symbol for you.'),
        )),
        'currency_position' => new BillingChoiceField(array(
            'label'   => $__('Symbol position'),
            'default' => 'after',
            'choices' => array(
                'after'  => $__('After the amount'),
                'before' => $__('Before the amount'),
            ),
        )),
        'decimal_sep' => new TextboxField(array(
            'label'   => $__('Decimal separator'),
            'default' => ',',
            'configuration' => array('size' => 2, 'length' => 1),
        )),
        'thousand_sep' => new TextboxField(array(
            'label'   => $__('Thousands separator'),
            'default' => '.',
            'configuration' => array('size' => 2, 'length' => 1),
        )),

        /* ---- Tickets / scope ---------------------------------------- */

        'status_filter' => new BillingLazyChoiceField(array(
            'label'   => $__('Restrict to ticket status'),
            'default' => '',
            // Built on render only - this reads the ticket_status table.
            'choices_callback' => function () use ($__) {
                return array('' => $__('All statuses'))
                    + array_map('strval', Billing::ticketStatuses());
            },
            'hint' => $__('Reports and exports then contain tickets in this status only — typically "closed", so that nothing still being worked on ends up on an invoice.'),
        )),

        /* ---- Tickets / ticket page ---------------------------------- */

        'link_ticket_view' => new BooleanField(array(
            'label'   => $__('Summary in the ticket menu'),
            'default' => true,
            'configuration' => array(
                'desc' => $__('Show recorded time, amount and a link to the invoice in the gear menu of a ticket.'),
            ),
            'hint' => $__('The time-type selector below the ticket details is always shown; it is what agents use to record billing information.'),
        )),
        'log_ticket_events' => new BooleanField(array(
            'label'   => $__('Log changes in the ticket'),
            'default' => true,
            'configuration' => array(
                'desc' => $__('Add a note to the ticket whenever the time type or the number of trips changes.'),
            ),
        )),
        'hide_open_items' => new BooleanField(array(
            'label'   => $__('Simple mode — no billing status'),
            'default' => false,
            'configuration' => array(
                'desc' => $__('Hide the "Open items" list and the billed / goodwill markers on tickets.'),
            ),
            'hint' => $__('Every ticket then counts as billable. Use this if you never mark tickets as billed and only need the reports and the export.'),
        )),

        /* ---- Tickets / access --------------------------------------- */

        'agent_access' => new BooleanField(array(
            'label'   => $__('Open billing to agents'),
            'default' => false,
            'configuration' => array(
                'desc' => $__('Let agents without admin rights open the Billing application and the organization billing.'),
            ),
            'hint' => $__('Off means administrators only. Time types and rates stay admin-only either way.'),
        )),

        /* ---- Report / export file ----------------------------------- */

        'export_filename' => new TextboxField(array(
            'label' => $__('File name'),
            'hint'  => $__('For the CSV and PDF download, without the extension. Placeholders are allowed; %{report.year}, %{report.month} and %{report.day} refer to the end of the period. Characters a file name cannot contain are replaced automatically.'),
            'default' => '%{report.org}_Leistungsnachweis_%{report.year}-%{report.month}',
            'configuration' => array('size' => 60, 'length' => 191),
        )),

        /* ---- Report / below the table ------------------------------- */

        'show_totals' => new BooleanField(array(
            'label'   => $__('Totals'),
            'default' => true,
            'configuration' => array(
                'desc' => $__('Print subtotal, tax and total under every table — in time-only mode, total and billable time.'),
            ),
        )),
        'table_footer_left' => new TextboxField(array(
            'label' => $__('Free line, left'),
            'hint'  => $__('Printed under the table on the left. Placeholders are allowed. Leave empty for nothing.'),
            'default' => $__('Total tickets: %{report.count}, trips: %{report.trips}'),
            'configuration' => array('size' => 60, 'length' => 255),
        )),
        'table_footer_right' => new TextboxField(array(
            'label' => $__('Free line, right'),
            'hint'  => $__('Printed under the table on the right. Placeholders are allowed. Leave empty for nothing.'),
            'default' => $__('Billable time: %{report.billable}'),
            'configuration' => array('size' => 60, 'length' => 255),
        )),

        /* ---- Report / additional block ------------------------------ */

        'export_footer_mode' => new BillingChoiceField(array(
            'label'   => $__('Additional block'),
            'default' => 'note',
            'choices' => array(
                'off'    => $__('No additional block'),
                'note'   => $__('Free-text note'),
                'checks' => $__('Checklist table'),
            ),
            'hint' => $__('Each organization edits its own block on its billing page; what you set here is what a new organization starts with.'),
        )),
        'note_default_text' => new TextareaField(array(
            'label' => $__('Starting text'),
            'hint'  => $__('Used for organizations that have no note yet, and printed for organizations that never write one.'),
            'default' => '',
            'configuration' => array('rows' => 5, 'cols' => 46, 'html' => false),
        )),
        'table_title' => new TextboxField(array(
            'label' => $__('Table heading'),
            'hint'  => $__('Printed above the table, and shown above the editor.'),
            'default' => $__('Inspection of customer systems'),
            'configuration' => array('size' => 60, 'length' => 191),
        )),
        'table_meta_text' => new TextboxField(array(
            'label' => $__('Last-modified line'),
            'hint'  => $__('Bold line above the table. %{date} is the time of the last change and %{by} the employee who made it; the report placeholders work here too. Leave empty to drop the line.'),
            'default' => $__('Last modified on: %{date} by %{by}'),
            'configuration' => array('size' => 60, 'length' => 191),
        )),
        'table_columns' => new TextareaField(array(
            'label' => $__('Columns'),
            'hint'  => $__('One heading per line. The first line heads the left-hand column that names each row; every further line becomes an editable column.'),
            'default' => "Prüfungen\nDatum letzte Prüfung\ngeprüft durch Techniker / Administrator\nBemerkungen",
            'configuration' => array('rows' => 5, 'cols' => 46, 'html' => false),
        )),
        'table_rows' => new TextareaField(array(
            'label' => $__('Rows'),
            'hint'  => $__('One row per line, each the label in the left-hand column. Organizations fill in the cells and can switch single rows off. Leave empty to start without rows.'),
            'default' => "Server-Hardware\nServer-Updates\nDatensicherung lokal\nDatensicherung extern\nSicherung TK-Anlage\nÜberprüfung NAS (Updates und Festplatten)\nAntivirenschutz",
            'configuration' => array('rows' => 8, 'cols' => 46, 'html' => false),
        )),

        /* ---- PDF / page --------------------------------------------- */

        'pdf_orientation' => new BillingChoiceField(array(
            'label'   => $__('Orientation'),
            'default' => 'L',
            'choices' => array('L' => $__('Landscape'), 'P' => $__('Portrait')),
            'hint'    => $__('Landscape fits more columns; portrait looks more like a letter.'),
        )),
        'pdf_page_size' => new BillingChoiceField(array(
            'label'   => $__('Page size'),
            'default' => 'A4',
            'choices' => array('A4' => 'A4', 'Letter' => 'Letter', 'A3' => 'A3'),
        )),
        'pdf_page_numbers' => new BooleanField(array(
            'label'   => $__('Page numbers'),
            'default' => true,
            'configuration' => array('desc' => $__('Print "Page x / y" in the footer.')),
        )),
        'pdf_show_meta' => new BooleanField(array(
            'label'   => $__('Filter summary'),
            'default' => true,
            'configuration' => array('desc' => $__('Print the generation date and the filters the report was built with.')),
        )),

        /* ---- PDF / letterhead --------------------------------------- */

        'pdf_logo_mode' => new BillingChoiceField(array(
            'label'   => $__('Logo'),
            'default' => 'none',
            'choices' => array(
                'none'     => $__('No logo'),
                'helpdesk' => $__('The helpdesk logo'),
                'upload'   => $__('A separate logo'),
            ),
        )),
        'pdf_logo_file' => new BillingLogoField(array(
            'label' => $__('Logo file'),
            'hint'  => $__('PNG, JPG or GIF. Uploaded straight away; the setting is kept when you save.'),
        )),
        'pdf_layout' => new BillingChoiceField(array(
            'label'   => $__('Arrangement'),
            'default' => 'logo_left',
            'choices' => array(
                'logo_left'   => $__('Logo left, text beside it'),
                'logo_top'    => $__('Logo left, text below'),
                'logo_center' => $__('Logo centred, text below'),
                'logo_right'  => $__('Logo right, text left'),
            ),
        )),
        'pdf_text_align' => new BillingChoiceField(array(
            'label'   => $__('Text alignment'),
            'default' => 'left',
            'choices' => array(
                'left'   => $__('Left'),
                'center' => $__('Centre'),
                'right'  => $__('Right'),
            ),
            'hint' => $__('Applies to title, subtitle, header text and footer text together.'),
        )),

        /* ---- PDF / texts -------------------------------------------- */

        'pdf_title' => new TextboxField(array(
            'label' => $__('Title'),
            'hint'  => $__('The heading at the very top. Leave empty for the built-in one.'),
            'configuration' => array('size' => 46, 'length' => 120),
        )),
        'pdf_subtitle' => new TextboxField(array(
            'label' => $__('Subtitle'),
            'hint'  => $__('Optional second line, for example your company name.'),
            'configuration' => array('size' => 46, 'length' => 120),
        )),
        'pdf_header_text' => new TextareaField(array(
            'label' => $__('Header text'),
            'hint'  => $__('Printed under the letterhead on the first page. Can be formatted in the editor.'),
            'configuration' => array('rows' => 4, 'cols' => 46, 'html' => true,
                                     'class' => 'richtext small'),
        )),
        'pdf_footer_text' => new TextareaField(array(
            'label' => $__('Footer text'),
            'hint'  => $__('Printed at the bottom of every page.'),
            'configuration' => array('rows' => 3, 'cols' => 46, 'html' => true,
                                     'class' => 'richtext small'),
        )),

        /* ---- System ------------------------------------------------- */

        'sys_status' => new BillingRawField(array(
            // Built on render only - this counts time types and probes tables.
            'configuration' => array('callback' => function () use ($__) {
                return $this->statusCard($__);
            }),
        )),
        'enable_diag' => new BooleanField(array(
            'label'   => $__('Diagnostics page'),
            'default' => false,
            'configuration' => array(
                'desc' => $__('Add a "Diagnostics" button to the billing overview.'),
            ),
            'hint' => $__('It measures how much time this plugin spends while a ticket is saved. Useful when tickets feel slow, pointless otherwise — leave it off.'),
        )),

        );
    }

    /** Live state of the installation, shown on the System tab. */
    private function statusCard($__) {
        $rows = array();

        $ts = class_exists('Billing') ? Billing::timesheetAvailable() : false;
        $rows[] = array($ts, $__('Time Recording'),
            $ts ? $__('Table ost_timesheet found — time entries can be billed.')
                : $__('Table ost_timesheet is missing. Install and enable the Time Recording plugin.'));

        $types = 0;
        if (class_exists('BillingTimeType'))
            $types = count(BillingTimeType::getActiveList());
        $rows[] = array($types > 0, $__('Time types'),
            $types > 0
                ? sprintf($__('Active time types: %d'), $types)
                : $__('No active time type. Nothing can be billed until at least one exists.'));

        $mode = BillingChoiceField::key($this->get('billing_mode', 'money')) === 'time'
            ? $__('Time only — hours, no amounts')
            : $__('Amounts — hourly rates, tax, totals');

        $html = '<div class="bx-status">';
        foreach ($rows as $r) {
            list($ok, $title, $text) = $r;
            $html .= '<div class="bx-status-row"><i class="'
                  .($ok ? 'icon-ok-sign bx-ok' : 'icon-warning-sign bx-bad').'"></i>'
                  .'<div><b>'.Format::htmlchars($title).'</b><br>'
                  .Format::htmlchars($text).'</div></div>';
        }
        $html .= '<div class="bx-status-row"><i class="icon-info-sign bx-info"></i>'
              .'<div><b>'.Format::htmlchars($__('Current model')).'</b><br>'
              .Format::htmlchars($mode).'</div></div>';
        $html .= '</div><div class="bx-actions">'
              .'<a class="button" href="'.ROOT_PATH.'scp/dispatcher.php/billing">'
              .'<i class="icon-dashboard"></i> '.Format::htmlchars($__('Open billing')).'</a>'
              .'<a class="button" href="'.ROOT_PATH.'scp/dispatcher.php/billing/timetypes">'
              .'<i class="icon-list"></i> '.Format::htmlchars($__('Manage time types')).'</a>';
        if ($this->get('enable_diag'))
            $html .= '<a class="button" href="'.ROOT_PATH.'scp/dispatcher.php/billing/diag">'
                  .'<i class="icon-beaker"></i> '.Format::htmlchars($__('Diagnostics')).'</a>';
        $html .= '</div>';

        return $html;
    }

    /* ------------------------------------------------------------------
       Saving
       ------------------------------------------------------------------ */

    function pre_save(&$config, &$errors) {
        global $msg;
        list($__, $_N) = self::translate();

        // Normalise numeric values.
        $config['tax_rate']     = (float) str_replace(',', '.', $config['tax_rate'] ?? '0');
        $config['default_rate'] = (float) str_replace(',', '.', $config['default_rate'] ?? '0');
        $inc = (int) ($config['round_increment'] ?? 0);
        $config['round_increment'] = in_array($inc, array(0, 15, 30, 45, 60), true) ? $inc : 0;
        $config['drop_below']   = max(0, (int) ($config['drop_below'] ?? 0));

        // A ready-made number format wins over the three detail settings;
        // "custom" leaves whatever was typed into them untouched.
        $fmt = (string) ($config['number_format'] ?? 'custom');
        if (isset(self::$numberFormats[$fmt]))
            foreach (self::$numberFormats[$fmt] as $key => $value)
                $config[$key] = $value;

        // Separators are single characters; an empty one would corrupt
        // every formatted amount.
        if (($config['decimal_sep'] ?? '') === '')  $config['decimal_sep'] = ',';
        $config['decimal_sep']  = mb_substr((string) $config['decimal_sep'], 0, 1);
        $config['thousand_sep'] = mb_substr((string) ($config['thousand_sep'] ?? ''), 0, 1);

        if (!$errors)
            $msg = $__('Billing settings saved');
        return true;
    }
}
