<?php
/*********************************************************************
    class.BillingConfigUi.php

    Presentation layer for the plugin's settings page.

    osTicket renders a plugin configuration as one long, flat list of
    fields (include/staff/templates/simple-form.tmpl.php). This class turns
    that list into a tabbed, card-based settings screen without touching
    a single core file:

      * config.php only declares WHAT can be configured (see fieldDefs()).
      * This file declares HOW it is presented: which tab, which card,
        in which order, and under which condition a setting is relevant.

    Everything is driven by ::layout(); assemble() then builds the flat
    field list osTicket expects, in exactly that order, and injects the
    stylesheet plus the small script that folds the list into tabs.

    If the script never runs (JavaScript off, markup changed by a future
    osTicket release), the page degrades to the plain list it always was -
    still complete, still styled, still saveable. Nothing is hidden by
    the server, so no setting can ever become unreachable.

    PHP 8.4 compatible.
**********************************************************************/

require_once(INCLUDE_DIR.'class.forms.php');

/**
 * Block-level field that prints raw HTML.
 *
 * Deliberately NOT FreeTextField: osTicket pipes that one through
 * Format::display(), which strips <style>/<script> and every id/data-*
 * attribute - exactly the markup a custom settings screen is made of.
 * This widget echoes its content verbatim; the content is authored here,
 * never by a user.
 */
if (!class_exists('BillingRawField')) {
    class BillingRawField extends FormField {
        static $widget = 'BillingRawWidget';
        function hasData()          { return false; }
        function isBlockLevel()     { return true; }
        function isEditableToStaff(){ return true; }
        function isEditableToUsers(){ return false; }
    }
    class BillingRawWidget extends Widget {
        function render($options = array()) {
            echo BillingConfigUi::content($this->field);
        }
    }
}

/**
 * Presentation-only field that renders inline in the value column, so a
 * button or a status line lines up with the other settings instead of
 * bleeding across the whole form.
 */
if (!class_exists('BillingInlineField')) {
    class BillingInlineField extends FormField {
        static $widget = 'BillingInlineWidget';
        function hasData()          { return false; }
        function isBlockLevel()     { return false; }
        function isEditableToStaff(){ return true; }
        function isEditableToUsers(){ return false; }
    }
    class BillingInlineWidget extends Widget {
        function render($options = array()) {
            echo BillingConfigUi::content($this->field);
        }
    }
}

/**
 * Choice field whose list is only built when the form is really rendered.
 *
 * osTicket instantiates a plugin's configuration on every single request
 * just to read the defaults, so a choice list that hits the database (ticket
 * statuses, time types) must not be built in the field definition itself.
 */
if (!class_exists('BillingLazyChoiceField')) {
    class BillingLazyChoiceField extends ChoiceField {
        function getChoices($verbose=false, $options=array()) {
            if (!$this->get('choices') && ($cb = $this->get('choices_callback'))
                    && is_callable($cb))
                $this->set('choices', call_user_func($cb));
            return parent::getChoices($verbose, $options);
        }
    }
}

/**
 * Self-contained logo upload.
 *
 * osTicket's native FileUploadField posts to ajax.php/form/upload/<id>,
 * which requires a DynamicFormField row in the database. Plugin config
 * fields have none, so that endpoint answers "400 No such field". We
 * therefore render our own control and post to a plugin route that stores
 * the image with AttachmentFile::uploadLogo(), the same call the core logo
 * setting uses.
 */
if (!class_exists('BillingLogoField')) {
    class BillingLogoField extends FormField {
        static $widget = 'BillingLogoWidget';
        function hasData()          { return true; }
        function isBlockLevel()     { return false; }
        function to_database($value){ return (string) $value; }
        function to_php($value)     { return (string) $value; }
    }

    class BillingLogoWidget extends Widget {
        function render($options = array()) {
            $id   = (int) $this->value;
            $name = '';
            if ($id && class_exists('AttachmentFile')
                    && ($f = AttachmentFile::lookup($id)))
                $name = $f->getName();
            list($__, $_N) = BillingConfigUi::translate();
            $u  = $this->id.'_up';
            $ph = $__('No file selected');
            ?>
            <div class="bx-upload" id="<?php echo $u; ?>_box">
              <input type="hidden" name="<?php echo $this->name; ?>"
                     id="<?php echo $this->id; ?>" value="<?php echo $id ?: ''; ?>">
              <label class="bx-upload-btn" for="<?php echo $u; ?>">
                <i class="icon-picture"></i> <?php echo $__('Choose image'); ?>
              </label>
              <input type="file" id="<?php echo $u; ?>" class="bx-upload-input"
                     accept="image/png,image/jpeg,image/gif">
              <span class="bx-upload-name<?php echo $name ? '' : ' bx-muted'; ?>"
                    id="<?php echo $u; ?>_s"><?php
                echo $name ? Format::htmlchars($name) : Format::htmlchars($ph); ?></span>
              <a href="#" class="bx-upload-clear<?php echo $id ? '' : ' bx-hide'; ?>"
                 id="<?php echo $u; ?>_x"><?php echo $__('Remove'); ?></a>
            </div>
            <script type="text/javascript">
            (function(){
                var inp = document.getElementById('<?php echo $u; ?>'),
                    hid = document.getElementById('<?php echo $this->id; ?>'),
                    st  = document.getElementById('<?php echo $u; ?>_s'),
                    rm  = document.getElementById('<?php echo $u; ?>_x');
                if (!inp || !hid || !st) return;
                function idle(text, muted) {
                    st.textContent = text;
                    st.className = 'bx-upload-name' + (muted ? ' bx-muted' : '');
                }
                if (rm) rm.addEventListener('click', function(e){
                    e.preventDefault();
                    hid.value = ''; inp.value = '';
                    idle(<?php echo json_encode($ph); ?>, true);
                    rm.className = 'bx-upload-clear bx-hide';
                });
                inp.addEventListener('change', function(){
                    if (!inp.files || !inp.files.length) return;
                    var fd = new FormData();
                    fd.append('upload', inp.files[0]);
                    // staff.inc.php rejects any POST without a valid CSRF token
                    var m = document.querySelector('meta[name=csrf_token]');
                    if (m) fd.append('__CSRFToken__', m.getAttribute('content'));
                    idle(<?php echo json_encode($__('Uploading...')); ?>, true);
                    fetch('<?php echo ROOT_PATH; ?>scp/dispatcher.php/billing/upload',
                          {method:'POST', body:fd, credentials:'same-origin'})
                    .then(function(r){ return r.json().catch(function(){ throw new Error(r.status); }); })
                    .then(function(j){
                        if (j && j.error) throw new Error(j.error);
                        if (!j || !j.id) throw new Error('no id');
                        hid.value = j.id;
                        idle(inp.files[0].name, false);
                        if (rm) rm.className = 'bx-upload-clear';
                    })
                    .catch(function(e){
                        idle(<?php echo json_encode($__('Upload failed')); ?>+': '+e.message, false);
                    });
                });
            })();
            </script>
            <?php
        }
    }
}

class BillingConfigUi {

    static function translate() {
        return Plugin::translate('billing');
    }

    /**
     * HTML of a raw field: either given outright, or produced by a callback
     * the first time the field is actually rendered.
     */
    static function content($field) {
        $c = $field->getConfiguration();
        if (isset($c['content']))
            return $c['content'];
        if (isset($c['callback']) && is_callable($c['callback']))
            return (string) call_user_func($c['callback']);
        return '';
    }

    /* ------------------------------------------------------------------
       The information architecture of the settings screen.

       tabs[]   id, title, icon, lead, groups[]
       groups[] id, title, icon, desc, fields[]

       "when" rules live in ::conditions(); everything referenced there is
       hidden by the browser as soon as it cannot apply, so a tab only ever
       shows the settings that actually do something right now.
       ------------------------------------------------------------------ */
    static function layout($__) {
        return array(
            array(
                'id' => 'billing', 'icon' => 'icon-money',
                'title' => $__('Billing'),
                'lead'  => $__('How recorded time is turned into an invoice.'),
                'groups' => array(
                    array('id'=>'model', 'icon'=>'icon-dashboard',
                          'title'=>$__('Billing model'),
                          'desc' =>$__('Decides whether this plugin works with amounts or with hours only. Everything else on this tab follows from it.'),
                          'fields'=>array('billing_mode','default_rate','tax_rate','manage_types')),
                    array('id'=>'rounding', 'icon'=>'icon-time',
                          'title'=>$__('Rounding'),
                          'desc' =>$__('Applied per time type, after all entries of that type have been added up.'),
                          'fields'=>array('round_increment','drop_below')),
                    array('id'=>'number', 'icon'=>'icon-globe',
                          'title'=>$__('Number format'),
                          'desc' =>$__('How amounts are written in reports, in the PDF and in the CSV export.'),
                          'fields'=>array('currency_symbol','number_format',
                                          'currency_position','decimal_sep','thousand_sep')),
                ),
            ),
            array(
                'id' => 'tickets', 'icon' => 'icon-tags',
                'title' => $__('Tickets'),
                'lead'  => $__('Which tickets are picked up, and what billing shows inside a ticket.'),
                'groups' => array(
                    array('id'=>'scope', 'icon'=>'icon-filter',
                          'title'=>$__('Scope'),
                          'desc' =>$__('Limits every report and export to a part of your tickets.'),
                          'fields'=>array('status_filter')),
                    array('id'=>'ticketpage', 'icon'=>'icon-file-alt',
                          'title'=>$__('On the ticket page'),
                          'fields'=>array('link_ticket_view','log_ticket_events','hide_open_items')),
                    array('id'=>'access', 'icon'=>'icon-lock',
                          'title'=>$__('Access'),
                          'fields'=>array('agent_access')),
                ),
            ),
            array(
                'id' => 'report', 'icon' => 'icon-file-text-alt',
                'title' => $__('Report & export'),
                'lead'  => $__('The document your customer gets - on screen, as CSV and as PDF.'),
                'groups' => array(
                    array('id'=>'file', 'icon'=>'icon-download-alt',
                          'title'=>$__('Export file'),
                          'fields'=>array('export_filename')),
                    array('id'=>'totals', 'icon'=>'icon-th-list',
                          'title'=>$__('Below the table'),
                          'desc' =>$__('Totals and the two free lines printed directly under every report table.'),
                          'fields'=>array('show_totals','table_footer_left','table_footer_right')),
                    array('id'=>'extra', 'icon'=>'icon-list-alt',
                          'title'=>$__('Additional block'),
                          'desc' =>$__('An optional block underneath the table that each organization fills in for itself - either a free-text note or a checklist table. The settings here are the starting point for organizations that have not edited theirs yet.'),
                          'fields'=>array('export_footer_mode','note_default_text',
                                          'table_title','table_meta_text',
                                          'table_columns','table_rows')),
                ),
            ),
            array(
                'id' => 'pdf', 'icon' => 'icon-print',
                'title' => $__('PDF layout'),
                'lead'  => $__('Applies to the PDF export only. The CSV export is unaffected.'),
                'groups' => array(
                    array('id'=>'page', 'icon'=>'icon-file-alt',
                          'title'=>$__('Page'),
                          'fields'=>array('pdf_orientation','pdf_page_size',
                                          'pdf_page_numbers','pdf_show_meta')),
                    array('id'=>'letterhead', 'icon'=>'icon-picture',
                          'title'=>$__('Letterhead'),
                          'desc' =>$__('The logo and text block at the top of the first page.'),
                          'fields'=>array('pdf_logo_mode','pdf_logo_file',
                                          'pdf_layout','pdf_text_align')),
                    array('id'=>'texts', 'icon'=>'icon-font',
                          'title'=>$__('Texts'),
                          'fields'=>array('pdf_title','pdf_subtitle',
                                          'pdf_header_text','pdf_footer_text')),
                ),
            ),
            array(
                'id' => 'system', 'icon' => 'icon-wrench',
                'title' => $__('System'),
                'lead'  => $__('Status of the installation and tools for troubleshooting.'),
                'groups' => array(
                    array('id'=>'status', 'icon'=>'icon-info-sign',
                          'title'=>$__('Status'),
                          'fields'=>array('sys_status')),
                    array('id'=>'diag', 'icon'=>'icon-beaker',
                          'title'=>$__('Troubleshooting'),
                          'fields'=>array('enable_diag')),
                ),
            ),
        );
    }

    /**
     * Rules that hide a setting while it cannot have any effect.
     *
     *   target  => array('field' => <other setting>, 'in' => array(values))
     *
     * A target starting with "@" is a whole card. Hiding is done in the
     * browser only - the inputs stay in the form and keep their value, so
     * switching a mode back never loses what was configured before.
     */
    static function conditions() {
        return array(
            'default_rate'       => array('field'=>'billing_mode',       'in'=>array('money')),
            'tax_rate'           => array('field'=>'billing_mode',       'in'=>array('money')),
            '@number'            => array('field'=>'billing_mode',       'in'=>array('money')),
            'currency_position'  => array('field'=>'number_format',      'in'=>array('custom')),
            'decimal_sep'        => array('field'=>'number_format',      'in'=>array('custom')),
            'thousand_sep'       => array('field'=>'number_format',      'in'=>array('custom')),
            'note_default_text'  => array('field'=>'export_footer_mode', 'in'=>array('note')),
            'table_title'        => array('field'=>'export_footer_mode', 'in'=>array('checks')),
            'table_meta_text'    => array('field'=>'export_footer_mode', 'in'=>array('checks')),
            'table_columns'      => array('field'=>'export_footer_mode', 'in'=>array('checks')),
            'table_rows'         => array('field'=>'export_footer_mode', 'in'=>array('checks')),
            'table_footer_left'  => array('field'=>'show_totals',        'in'=>array('1')),
            'table_footer_right' => array('field'=>'show_totals',        'in'=>array('1')),
            'pdf_logo_file'      => array('field'=>'pdf_logo_mode',      'in'=>array('upload')),
            'pdf_layout'         => array('field'=>'pdf_logo_mode',      'in'=>array('helpdesk','upload')),
        );
    }

    /** Tabs on which the placeholder reference is worth showing. */
    static function placeholderTabs() {
        return array('report', 'pdf');
    }

    /** Placeholders understood by every text field of this plugin. */
    static function placeholders($__) {
        return array(
            $__('Organization')    => '%{report.org}',
            $__('From')            => '%{report.from}',
            $__('To')              => '%{report.to}',
            $__('Date')            => '%{report.date}',
            $__('Year')            => '%{report.year}',
            $__('Month')           => '%{report.month}',
            $__('Day')             => '%{report.day}',
            $__('Number of rows')  => '%{report.count}',
            $__('Trips')           => '%{report.trips}',
            $__('Travel charge')   => '%{report.travel}',
            $__('Tickets on-site') => '%{report.onsite}',
            $__('Tickets remote')  => '%{report.office}',
            $__('Time')            => '%{report.time}',
            $__('Billable time')   => '%{report.billable}',
            $__('Subtotal')        => '%{report.subtotal}',
            $__('Tax rate (%)')    => '%{report.tax_rate}',
            $__('Tax')             => '%{report.tax}',
            $__('Total')           => '%{report.total}',
        );
    }

    /* ------------------------------------------------------------------
       Assembly
       ------------------------------------------------------------------ */

    /**
     * Build the flat field list osTicket renders, in layout order:
     * boot field, then per tab a tab marker, then per group a group marker
     * followed by that group's settings.
     *
     * Any field that ::layout() forgot is appended to a final card instead
     * of silently disappearing - a missing setting is a bug, an unreachable
     * setting is a support case.
     */
    static function assemble(array $defs, $__, $notice = '') {
        $boot = new BillingRawField(array());
        $out  = array('bx_boot' => $boot);
        if ($notice)
            $out['bx_notice'] = self::marker($notice);
        $used  = array();
        $block = array();

        foreach (self::layout($__) as $tab) {
            $out['t_'.$tab['id']] = self::marker(
                '<div class="bx-th" data-bx-tab="'.$tab['id'].'"'
                .' data-bx-icon="'.Format::htmlchars($tab['icon']).'"'
                .' data-bx-title="'.Format::htmlchars($tab['title']).'">'
                .'<h2>'.Format::htmlchars($tab['title']).'</h2>'
                .'<p>'.Format::htmlchars($tab['lead']).'</p></div>');

            foreach ($tab['groups'] as $g) {
                $desc = isset($g['desc']) ? $g['desc'] : '';
                $out['g_'.$g['id']] = self::marker(
                    '<div class="bx-gh" data-bx-group="'.$g['id'].'">'
                    .'<h3><i class="'.Format::htmlchars($g['icon']).'"></i> '
                    .Format::htmlchars($g['title']).'</h3>'
                    .($desc ? '<p>'.Format::htmlchars($desc).'</p>' : '')
                    .'</div>');

                foreach ($g['fields'] as $key) {
                    if (isset($defs[$key])) {
                        $out[$key] = $defs[$key];
                        $used[$key] = true;
                        if ($defs[$key]->isBlockLevel())
                            $block[] = $key;
                    }
                }
            }
        }

        $rest = array_diff_key($defs, $used);
        if ($rest) {
            $out['t_more'] = self::marker(
                '<div class="bx-th" data-bx-tab="more" data-bx-icon="icon-question-sign"'
                .' data-bx-title="'.Format::htmlchars($__('Other')).'">'
                .'<h2>'.Format::htmlchars($__('Other')).'</h2>'
                .'<p>'.Format::htmlchars($__('Settings that are not part of a group yet.')).'</p></div>');
            $out['g_more'] = self::marker(
                '<div class="bx-gh" data-bx-group="more"><h3><i class="icon-question-sign"></i> '
                .Format::htmlchars($__('Other')).'</h3></div>');
            foreach ($rest as $k => $f) {
                $out[$k] = $f;
                if ($f->isBlockLevel())
                    $block[] = $k;
            }
        }

        $out['bx_ph'] = new BillingRawField(array(
            'configuration' => array('content' => self::placeholderPanel($__)),
        ));

        // Filled in last: the script needs to know which settings render
        // without a label column.
        $boot->set('configuration', array('content' => self::assets($__, $block)));

        return $out;
    }

    private static function marker($html) {
        return new BillingRawField(array('configuration' => array('content' => $html)));
    }

    /** Collapsible placeholder reference, pinned under the form. */
    private static function placeholderPanel($__) {
        $rows = '';
        foreach (self::placeholders($__) as $label => $token)
            $rows .= '<button type="button" class="bx-token" data-bx-token="'
                  .Format::htmlchars($token).'"><code>'.Format::htmlchars($token)
                  .'</code><span>'.Format::htmlchars($label).'</span></button>';

        return '<details class="bx-ph" id="bx-ph">'
            .'<summary><i class="icon-tags"></i> '.Format::htmlchars($__('Placeholders'))
            .' <em>'.Format::htmlchars($__('usable in every text field on this page')).'</em></summary>'
            .'<div class="bx-ph-body"><div class="bx-tokens">'.$rows.'</div>'
            .'<p class="bx-ph-hint">'.Format::htmlchars(
                $__('Click a placeholder to insert it at the cursor of the field you edited last.'))
            .'</p></div></details>';
    }

    /* ------------------------------------------------------------------
       Stylesheet and behaviour
       ------------------------------------------------------------------ */

    private static function assets($__, array $block = array()) {
        $cfg = array(
            'layout'     => array(),
            'conditions' => self::conditions(),
            'phTabs'     => self::placeholderTabs(),
            'block'      => $block,
            'i18n'       => array(
                'errors' => $__('This tab contains an invalid value.'),
            ),
        );
        foreach (self::layout($__) as $tab) {
            $groups = array();
            foreach ($tab['groups'] as $g)
                $groups[] = array('id' => $g['id'], 'fields' => $g['fields']);
            $cfg['layout'][] = array(
                'id'     => $tab['id'],
                'icon'   => $tab['icon'],
                'title'  => $tab['title'],
                'groups' => $groups,
            );
        }

        return '<style type="text/css">'.self::css().'</style>'
             .'<script type="text/javascript">window.BX_CONFIG='
             .json_encode($cfg, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)
             .';</script>'
             .'<script type="text/javascript">'.self::js().'</script>';
    }

    private static function css() {
        return <<<'CSS'
.bx-root{max-width:1100px;font-size:13px;color:#26313c;}
.bx-root *,.bx-root *:before,.bx-root *:after{box-sizing:border-box;}
.bx-root .bx-hide{display:none!important;}
.bx-root .bx-muted{color:#8a949e;}

/* ---- tab bar ---------------------------------------------------- */
.bx-nav{display:flex;flex-wrap:wrap;gap:2px;margin:14px 0 20px;
  border-bottom:2px solid #e3e8ed;}
.bx-nav button{appearance:none;-webkit-appearance:none;background:none;border:0;
  border-bottom:3px solid transparent;margin:0 0 -2px;padding:10px 16px;
  font:inherit;font-size:13px;font-weight:600;color:#5d6a76;cursor:pointer;
  border-radius:7px 7px 0 0;display:inline-flex;align-items:center;gap:8px;
  transition:background .12s,color .12s;}
.bx-nav button:hover{background:#f1f5f8;color:#1d2731;}
.bx-nav button.on{color:#12202c;background:#fff;border-bottom-color:#e86800;}
.bx-nav button i{opacity:.75;}
.bx-nav button.on i{opacity:1;color:#e86800;}
.bx-nav .bx-badge{width:7px;height:7px;border-radius:50%;background:#cc3300;
  display:inline-block;}

/* ---- panels ----------------------------------------------------- */
.bx-panel{display:none;}
.bx-panel.on{display:block;}
.bx-lead{margin:0 0 18px;color:#6d7883;font-size:12.5px;line-height:1.5;}

/* ---- cards ------------------------------------------------------ */
.bx-card{border:1px solid #e0e5ea;border-radius:10px;background:#fff;
  margin:0 0 16px;box-shadow:0 1px 2px rgba(16,24,32,.05);overflow:hidden;}
.bx-card > .bx-fields{padding:2px 16px 8px;}

/* group and tab headings - these also carry the page when the script
   never runs, so they are styled on their own, not only inside a card */
.bx-gh{margin:22px 0 8px;padding:9px 13px;background:#f2f5f8;
  border-left:4px solid #e86800;border-radius:0 5px 5px 0;}
.bx-gh h3{margin:0;font-size:13px;font-weight:700;color:#22303d;
  display:flex;align-items:center;gap:8px;}
.bx-gh h3 i{color:#7d8b98;font-size:13px;}
.bx-gh p{margin:5px 0 0;color:#6d7883;font-size:12px;line-height:1.5;}
.bx-th h2{margin:28px 0 2px;font-size:17px;color:#12202c;}
.bx-th p{margin:0 0 6px;color:#6d7883;font-size:12.5px;}
.bx-card > .bx-gh{margin:0;padding:11px 16px;background:#f7f9fb;border-left:0;
  border-radius:0;border-bottom:1px solid #e8edf1;}

/* ---- one setting ------------------------------------------------ */
.bx-root .bx-fields > .form-field{display:flex;flex-wrap:wrap;
  align-items:flex-start;gap:2px 20px;margin:0;padding:13px 0;
  border-top:1px solid #f0f3f6;}
.bx-root .bx-fields > .form-field:first-child{border-top:0;}
.bx-root .bx-fields > .form-field.bx-block{display:block;padding:14px 0;}
.bx-root .bx-fields > .form-field:not(.bx-block) > div:first-child{display:block!important;
  width:280px!important;max-width:280px!important;flex:0 0 280px;
  padding:2px 0 0!important;font-weight:600;font-size:12.5px;color:#2a3742;}
.bx-root .bx-fields > .form-field:not(.bx-block) > div + div{display:block!important;
  flex:1 1 340px;min-width:0;max-width:none!important;padding:0!important;}
.bx-root .bx-fields > .form-field .hint{margin-top:4px;font-weight:400;
  font-size:11.5px;line-height:1.5;color:#7b8792;}
.bx-root .bx-fields > .form-field .error{margin-top:5px;font-size:12px;}
.bx-root .bx-fields > .form-field .hint.bx-hint-below{margin-top:7px;max-width:640px;}
.bx-root .form-field.bx-off{display:none!important;}
.bx-root .bx-card.bx-off{display:none!important;}

/* full-width rows for the wide editors */
.bx-root .bx-fields > .form-field.bx-wide{display:block;}
.bx-root .bx-fields > .form-field.bx-wide > div:first-child{width:auto!important;
  max-width:none!important;margin-bottom:7px;}

/* ---- controls --------------------------------------------------- */
.bx-root .bx-fields input[type=text],
.bx-root .bx-fields input[type=number],
.bx-root .bx-fields select,
.bx-root .bx-fields textarea{font-size:13px;padding:5px 8px;
  border:1px solid #ccd4dc;border-radius:6px;background:#fff;color:#1f2a35;
  max-width:100%;}
.bx-root .bx-fields input[type=text]:focus,
.bx-root .bx-fields select:focus,
.bx-root .bx-fields textarea:focus{border-color:#e86800;outline:0;
  box-shadow:0 0 0 3px rgba(232,104,0,.12);}
.bx-root .bx-fields textarea{width:100%;max-width:640px;line-height:1.5;
  font-family:inherit;}
.bx-root .bx-fields label.checkbox{display:flex;gap:9px;align-items:flex-start;
  font-size:12.5px;line-height:1.5;color:#3a4753;cursor:pointer;margin:1px 0 0;}
.bx-root .bx-fields label.checkbox input{margin:2px 0 0;flex:0 0 auto;}
.bx-root .bx-fields .redactor-box{width:100%;max-width:640px;margin:0;}
.bx-root .bx-fields .redactor-box .redactor-in{min-height:120px!important;}
.bx-root .bx-fields a.button,.bx-root .bx-fields .bx-upload-btn{
  display:inline-flex;align-items:center;gap:7px;padding:6px 13px;
  border:1px solid #ccd4dc;border-radius:6px;background:#f7f9fb;
  color:#26313c;font-size:12.5px;font-weight:600;text-decoration:none;
  cursor:pointer;}
.bx-root .bx-fields a.button:hover,.bx-root .bx-fields .bx-upload-btn:hover{
  background:#eef2f6;border-color:#b9c4ce;}

/* ---- logo upload ------------------------------------------------ */
.bx-upload{display:flex;align-items:center;gap:10px;flex-wrap:wrap;}
.bx-upload-input{position:absolute;width:1px;height:1px;opacity:0;
  overflow:hidden;clip:rect(0 0 0 0);}
.bx-upload-name{font-size:12.5px;}
.bx-upload-clear{font-size:12px;}

/* ---- status card ------------------------------------------------ */
.bx-status{display:flex;flex-direction:column;gap:9px;padding:4px 0 10px;}
.bx-status-row{display:flex;align-items:flex-start;gap:10px;font-size:12.5px;
  line-height:1.5;}
.bx-status-row i{margin-top:1px;}
.bx-ok{color:#1f8a4c;} .bx-bad{color:#c0392b;} .bx-info{color:#7d8b98;}
.bx-status-row b{font-weight:600;}
.bx-actions{display:flex;gap:8px;flex-wrap:wrap;padding:4px 0 10px;}

/* ---- notice ----------------------------------------------------- */
.bx-notice{display:flex;gap:12px;align-items:flex-start;margin:14px 0 0;
  padding:12px 14px;border:1px solid #f0c9a8;border-left:4px solid #e86800;
  border-radius:8px;background:#fff8f2;font-size:12.5px;line-height:1.55;
  color:#5d4632;}
.bx-notice i{font-size:15px;color:#e86800;margin-top:1px;}
.bx-notice b{display:block;margin-bottom:2px;color:#3d2d1f;}

/* ---- placeholder reference -------------------------------------- */
.bx-ph{margin:6px 0 4px;border:1px solid #e0e5ea;border-radius:10px;
  background:#fbfcfd;}
.bx-ph[hidden]{display:none;}
.bx-ph > summary{cursor:pointer;padding:10px 14px;font-size:12.5px;
  font-weight:600;color:#3a4753;list-style:none;display:flex;
  align-items:center;gap:8px;}
.bx-ph > summary::-webkit-details-marker{display:none;}
.bx-ph > summary i{color:#7d8b98;}
.bx-ph > summary em{font-weight:400;font-style:normal;color:#8a949e;font-size:12px;}
.bx-ph-body{padding:0 14px 12px;}
.bx-tokens{display:grid;grid-template-columns:repeat(auto-fill,minmax(215px,1fr));
  gap:6px;}
.bx-token{display:flex;align-items:baseline;gap:8px;width:100%;text-align:left;
  padding:5px 8px;border:1px solid #e4e9ee;border-radius:6px;background:#fff;
  font:inherit;cursor:pointer;}
.bx-token:hover{border-color:#e86800;background:#fff8f2;}
.bx-token code{font-size:11.5px;color:#0a64a4;white-space:nowrap;}
.bx-token span{font-size:11.5px;color:#7b8792;overflow:hidden;
  text-overflow:ellipsis;white-space:nowrap;}
.bx-ph-hint{margin:9px 0 0;font-size:11.5px;color:#8a949e;}

@media (max-width:760px){
  .bx-root .bx-fields > .form-field > div:first-child{width:auto!important;
    max-width:none!important;flex:1 1 100%;}
}
CSS;
    }

    private static function js() {
        return <<<'JS'
(function(){
  var CFG = window.BX_CONFIG;
  if (!CFG) return;

  function ready(fn){
    if (document.readyState !== 'loading') fn();
    else document.addEventListener('DOMContentLoaded', fn);
  }

  function build(){
    var boot = document.getElementById('field_bx_boot');
    if (!boot || !boot.parentNode) return;
    var root = boot.parentNode;
    if (root.getAttribute('data-bx-ready') === '1') return;
    root.setAttribute('data-bx-ready', '1');
    root.className += ' bx-root';

    var anchor = document.getElementById('field_bx_notice') || boot;
    var nav = document.createElement('div'); nav.className = 'bx-nav';
    var body = document.createElement('div'); body.className = 'bx-body';
    root.insertBefore(nav, anchor.nextSibling);
    root.insertBefore(body, nav.nextSibling);

    var panels = {}, buttons = {}, cards = {};

    CFG.layout.forEach(function(tab){
      var panel = document.createElement('div');
      panel.className = 'bx-panel';
      panel.setAttribute('data-bx-panel', tab.id);

      // the tab marker carries the lead paragraph; reuse it, drop the h2
      var marker = document.getElementById('field_t_' + tab.id);
      if (marker) {
        var lead = marker.querySelector('.bx-th p');
        if (lead) { lead.className = 'bx-lead'; panel.appendChild(lead); }
        marker.parentNode.removeChild(marker);
      }

      tab.groups.forEach(function(g){
        var card = document.createElement('div');
        card.className = 'bx-card';
        card.setAttribute('data-bx-card', g.id);

        var gm = document.getElementById('field_g_' + g.id);
        if (gm) {
          var head = gm.querySelector('.bx-gh');
          if (head) card.appendChild(head);
          gm.parentNode.removeChild(gm);
        }

        var fields = document.createElement('div');
        fields.className = 'bx-fields';
        card.appendChild(fields);

        var moved = 0;
        g.fields.forEach(function(key){
          var el = document.getElementById('field_' + key);
          if (!el) return;
          if (CFG.block.indexOf(key) !== -1) {
            el.className += ' bx-block';
          } else {
            if (el.querySelector('textarea, .redactor-box'))
              el.className += ' bx-wide';
            // A hint longer than the label column reads far better under the
            // control than as a tall paragraph squeezed next to it.
            var hint = el.querySelector('.hint'), val = el.children[1];
            if (hint && val && (hint.textContent || '').trim().length > 110) {
              hint.className += ' bx-hint-below';
              val.appendChild(hint);
            }
          }
          fields.appendChild(el);
          moved++;
        });

        if (moved) { panel.appendChild(card); cards[g.id] = card; }
      });

      if (!panel.querySelector('.bx-card')) return;
      body.appendChild(panel);
      panels[tab.id] = panel;

      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('data-bx-go', tab.id);
      b.innerHTML = '<i class="' + tab.icon + '"></i><span></span>';
      b.querySelector('span').textContent = tab.title;
      b.addEventListener('click', function(){ show(tab.id); });
      nav.appendChild(b);
      buttons[tab.id] = b;
    });

    boot.className += ' bx-hide';

    /* ---- placeholder reference, pinned below the panels ---------- */
    var ph = document.getElementById('field_bx_ph');
    if (ph) { root.appendChild(ph); ph.className = 'bx-ph-wrap'; }

    var current = null;
    function show(id){
      if (!panels[id]) return;
      current = id;
      for (var k in panels) {
        panels[k].className = 'bx-panel' + (k === id ? ' on' : '');
        buttons[k].className = (k === id ? 'on' : '');
      }
      if (ph) {
        var on = CFG.phTabs.indexOf(id) !== -1;
        ph.style.display = on ? '' : 'none';
      }
      try { sessionStorage.setItem('bx-tab', id); } catch(e) {}
    }

    /* ---- conditional visibility ---------------------------------- */
    var conds = CFG.conditions || {};
    function valueOf(name){
      var el = document.getElementById('_' + name);
      if (!el) return null;
      if (el.type === 'checkbox') return el.checked ? '1' : '0';
      return el.value;
    }
    function apply(){
      for (var target in conds) {
        var rule = conds[target],
            v = valueOf(rule.field),
            on = (v === null) || rule.in.indexOf(String(v)) !== -1,
            el = target.charAt(0) === '@'
               ? cards[target.substring(1)]
               : document.getElementById('field_' + target);
        if (!el) continue;
        el.className = el.className.replace(/\s*bx-off\b/g, '') + (on ? '' : ' bx-off');
      }
    }
    var watched = {};
    for (var t in conds) watched[conds[t].field] = true;
    for (var w in watched) {
      var ctl = document.getElementById('_' + w);
      if (!ctl) continue;
      ctl.addEventListener('change', apply);
      ctl.addEventListener('input', apply);
    }
    apply();

    /* ---- placeholder insertion ----------------------------------- */
    var lastField = null;
    root.addEventListener('focusin', function(e){
      var t = e.target;
      if (t && (t.tagName === 'TEXTAREA'
            || (t.tagName === 'INPUT' && t.type === 'text')))
        lastField = t;
    });
    root.addEventListener('click', function(e){
      var b = e.target.closest ? e.target.closest('.bx-token') : null;
      if (!b) return;
      e.preventDefault();
      var token = b.getAttribute('data-bx-token');
      if (lastField && document.contains(lastField)) {
        var s = lastField.selectionStart, en = lastField.selectionEnd, v = lastField.value;
        if (typeof s === 'number') {
          lastField.value = v.slice(0, s) + token + v.slice(en);
          lastField.selectionStart = lastField.selectionEnd = s + token.length;
        } else {
          lastField.value = v + token;
        }
        lastField.focus();
      } else if (navigator.clipboard) {
        navigator.clipboard.writeText(token);
      }
    });

    /* ---- first tab: the one with an error, else the last used ---- */
    var start = null;
    var bad = root.querySelector('.bx-panel .form-field .error');
    if (bad) {
      var p = bad.closest ? bad.closest('.bx-panel') : null;
      if (p) {
        start = p.getAttribute('data-bx-panel');
        var dot = document.createElement('span');
        dot.className = 'bx-badge';
        if (buttons[start]) {
          buttons[start].appendChild(dot);
          buttons[start].title = CFG.i18n.errors;
        }
      }
    }
    if (!start) {
      try { start = sessionStorage.getItem('bx-tab'); } catch(e) {}
    }
    if (!start || !panels[start]) start = CFG.layout[0].id;
    show(start);
  }

  ready(function(){ try { build(); } catch (e) { if (window.console) console.warn('billing settings ui:', e); } });
})();
JS;
    }
}
