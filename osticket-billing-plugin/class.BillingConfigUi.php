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
 * Drop-down setting that stays a plain scalar.
 *
 * osTicket's ChoiceField is built for form answers, not for settings: it
 * stores the selection as {"key":"Label"} and hands it back as an array.
 * Every reader in this plugin compares a scalar - $config->get('billing_mode')
 * === 'time', (int) $config->get('round_increment') - so an array silently
 * fails every comparison and the setting falls back to its default. That is
 * why "Time only", the rounding increment and the symbol position had no
 * effect once the configuration had been saved a first time.
 *
 * Storing and returning the bare key fixes that, and to_php() still accepts
 * the {"key":"Label"} rows written by earlier versions, so existing
 * installations heal on the next read - no migration needed.
 */
if (!class_exists('BillingChoiceField')) {
    class BillingChoiceField extends ChoiceField {
        /** Reduce anything - array, JSON, scalar - to the bare choice key. */
        static function key($value) {
            if (is_string($value) && class_exists('JsonDataParser')
                    && ($j = JsonDataParser::parse($value)) && is_array($j))
                $value = $j;
            if (is_array($value)) {
                if (!$value)
                    return '';
                reset($value);
                return (string) key($value);
            }
            return (string) $value;
        }
        function to_database($value) { return self::key($value); }
        function to_php($value)      { return self::key($value); }
        function parse($value)       { return self::key($value); }
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
    class BillingLazyChoiceField extends BillingChoiceField {
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
            <input type="hidden" name="<?php echo $this->name; ?>"
                   id="<?php echo $this->id; ?>" value="<?php echo $id ?: ''; ?>">
            <input type="file" id="<?php echo $u; ?>"
                   accept="image/png,image/jpeg,image/gif">
            <span id="<?php echo $u; ?>_s" class="faded"><?php
                echo $name ? Format::htmlchars($name) : Format::htmlchars($ph); ?></span>
            <a href="#" id="<?php echo $u; ?>_x"<?php
                echo $id ? '' : ' class="hidden"'; ?>><?php echo $__('Remove'); ?></a>
            <script type="text/javascript">
            (function(){
                var inp = document.getElementById('<?php echo $u; ?>'),
                    hid = document.getElementById('<?php echo $this->id; ?>'),
                    st  = document.getElementById('<?php echo $u; ?>_s'),
                    rm  = document.getElementById('<?php echo $u; ?>_x');
                if (!inp || !hid || !st) return;
                function idle(text, faded) {
                    st.textContent = text;
                    st.className = faded ? 'faded' : '';
                }
                if (rm) rm.addEventListener('click', function(e){
                    e.preventDefault();
                    hid.value = ''; inp.value = '';
                    idle(<?php echo json_encode($ph); ?>, true);
                    rm.className = 'hidden';
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
                        if (rm) rm.className = '';
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
                '<div class="bx-th"><h2>'.Format::htmlchars($tab['title']).'</h2>'
                .'<p class="faded">'.Format::htmlchars($tab['lead']).'</p></div>');

            foreach ($tab['groups'] as $g) {
                $desc = isset($g['desc']) ? $g['desc'] : '';
                // Same markup osTicket's own SectionBreakField produces, so
                // the grey header bar is the native one, not a lookalike.
                $out['g_'.$g['id']] = self::marker(
                    '<div class="form-header section-break">'
                    .'<h3><i class="'.Format::htmlchars($g['icon']).'"></i> '
                    .Format::htmlchars($g['title']).'</h3>'
                    .($desc ? '<em>'.Format::htmlchars($desc).'</em>' : '')
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
                '<div class="bx-th"><h2>'.Format::htmlchars($__('Other')).'</h2>'
                .'<p class="faded">'
                .Format::htmlchars($__('Settings that are not part of a group yet.'))
                .'</p></div>');
            $out['g_more'] = self::marker(
                '<div class="form-header section-break"><h3><i class="icon-question-sign"></i> '
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

        // Filled in last: the script needs the render order (that is how it
        // finds the fields at all, see assets()) and which of them render
        // without a label column.
        $boot->set('configuration', array(
            'content' => self::assets($__, $block, array_keys($out)),
        ));

        return $out;
    }

    private static function marker($html) {
        return new BillingRawField(array('configuration' => array('content' => $html)));
    }

    /** Collapsible placeholder reference, pinned under the form. */
    private static function placeholderPanel($__) {
        $rows = '';
        foreach (self::placeholders($__) as $label => $token)
            $rows .= '<li><a href="#" class="bx-token" data-bx-token="'
                  .Format::htmlchars($token).'"><code>'.Format::htmlchars($token)
                  .'</code></a> <span class="faded">'.Format::htmlchars($label)
                  .'</span></li>';

        return '<div class="form-header section-break"><h3><i class="icon-tags"></i> '
            .Format::htmlchars($__('Placeholders')).'</h3><em>'
            .Format::htmlchars($__('usable in every text field on this page')).'</em></div>'
            .'<ul class="bx-tokens">'.$rows.'</ul>'
            .'<p class="faded">'.Format::htmlchars(
                $__('Click a placeholder to insert it at the cursor of the field you edited last.'))
            .'</p>';
    }

    /* ------------------------------------------------------------------
       Stylesheet and behaviour
       ------------------------------------------------------------------ */

    private static function assets($__, array $block = array(), array $order = array()) {
        $cfg = array(
            'layout'     => array(),
            'conditions' => self::conditions(),
            'phTabs'     => self::placeholderTabs(),
            'block'      => $block,
            // Render order of every field, which is how the script maps the
            // markup back to setting names - see the note in js().
            'order'      => $order,
            // Lets the number-format presets fill in the detail fields live.
            'formats'    => class_exists('BillingConfig') ? BillingConfig::$numberFormats : array(),
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

        return '<style type="text/css" id="bx-css">'.self::css().'</style>'
             .'<script type="text/javascript">window.BX_CONFIG='
             .json_encode($cfg, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)
             .';</script>'
             .'<script type="text/javascript">'.self::js().'</script>';
    }

    private static function css() {
        // Deliberately tiny. The tab bar (ul.clean.tabs), the grey group
        // headers (div.section-break), the warning banner (#msg_warning),
        // .hidden and .faded all come from osTicket's own stylesheet, so the
        // page looks like every other admin page. What is left here is only
        // what osTicket has no class for.
        return <<<'CSS'
.bx-th h2{margin:0 0 2px;}
.bx-th p{margin:0 0 12px;}
.bx-tokens{list-style:none;margin:8px 0;padding:0;
  display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:4px 16px;}
.bx-tokens li{margin:0;padding:0;font-size:95%;}
.bx-tokens code{font-size:95%;}
.bx-status p{margin:0 0 8px;}
.bx-status i{margin-right:5px;}
.bx-ok{color:#0a0;} .bx-bad{color:#a00;}
.bx-actions{margin:12px 0 4px;}
.bx-actions a{margin-right:6px;}
CSS;
    }

    private static function js() {
        return <<<'JS'
(function(){
  var CFG = window.BX_CONFIG;
  if (!CFG || !CFG.order || !CFG.order.length) return;

  // Must be read while this script is running - currentScript is null later.
  var SELF = document.currentScript || null;

  function ready(fn){
    if (document.readyState !== 'loading') fn();
    else document.addEventListener('DOMContentLoaded', fn);
  }

  /**
   * osTicket names every form field with a session-dependent md5 hash
   * (Form::getFormId() is 0, which is numeric, so FormField::getFormName()
   * hashes), and simple-form.tmpl.php builds the wrapper id from that name.
   * So the wrappers cannot be looked up by setting name.
   *
   * What is stable is the order: the template renders exactly one
   * .form-field per field, in the order getFields() returned them. We find
   * our own wrapper - the one holding this script - and count from there.
   */
  function locate(){
    if (SELF && SELF.closest) {
      var own = SELF.closest('.form-field');
      if (own) return own;
    }
    var css = document.getElementById('bx-css');
    if (css && css.closest) {
      var viaCss = css.closest('.form-field');
      if (viaCss) return viaCss;
    }
    return null;
  }

  function build(){
    var boot = locate();
    if (!boot || !boot.parentNode) return;
    var root = boot.parentNode;
    if (root.getAttribute('data-bx-ready') === '1') return;

    var wrappers = [];
    for (var n = root.firstElementChild; n; n = n.nextElementSibling)
      if ((' ' + (n.className || '') + ' ').indexOf(' form-field ') !== -1)
        wrappers.push(n);

    var offset = wrappers.indexOf(boot);
    if (offset < 0 || wrappers.length - offset !== CFG.order.length)
      return;   // not the markup we expect - leave the plain list alone

    var by = {};
    for (var i = 0; i < CFG.order.length; i++)
      by[CFG.order[i]] = wrappers[offset + i];

    function control(key){
      var w = by[key];
      return w ? w.querySelector('select, input[type=checkbox], input[type=text], textarea') : null;
    }
    function setHidden(el, hide){
      if (!el) return;
      var c = (' ' + el.className + ' ').replace(/ hidden /g, ' ').replace(/\s+/g, ' ').trim();
      el.className = hide ? (c + ' hidden') : c;
    }

    root.setAttribute('data-bx-ready', '1');

    /* ---- osTicket's own tab component ---------------------------- */
    var anchor = by['bx_notice'] || boot;
    var ul = document.createElement('ul');
    ul.className = 'clean tabs';
    ul.id = 'bx-tabs';
    var container = document.createElement('div');
    container.id = 'bx-tabs_container';   // scp.js finds panels by this name
    root.insertBefore(ul, anchor.nextSibling);
    root.insertBefore(container, ul.nextSibling);

    var panels = {}, items = {}, order = [], groups = {};

    CFG.layout.forEach(function(tab){
      var panel = document.createElement('div');
      panel.className = 'tab_content';
      panel.id = 'bx-tab-' + tab.id;

      var marker = by['t_' + tab.id];
      if (marker) {
        var lead = marker.querySelector('.bx-th p');
        if (lead) panel.appendChild(lead);      // keep the lead, drop the h2
        marker.parentNode.removeChild(marker);
      }

      var moved = 0;
      tab.groups.forEach(function(g){
        var gm = by['g_' + g.id], head = gm ? gm.querySelector('.section-break') : null;
        var placed = [];
        g.fields.forEach(function(key){
          if (by[key]) placed.push(by[key]);
        });
        if (!placed.length) return;
        if (head) panel.appendChild(head);
        if (gm) gm.parentNode.removeChild(gm);
        placed.forEach(function(el){ panel.appendChild(el); moved++; });
        groups[g.id] = { head: head, fields: placed };
      });
      if (!moved) return;

      container.appendChild(panel);
      panels[tab.id] = panel;
      order.push(tab.id);

      var li = document.createElement('li');
      var a  = document.createElement('a');
      a.href = '#' + panel.id;                  // scp.js switches on this
      a.innerHTML = '<i class="' + tab.icon + '"></i> ';
      a.appendChild(document.createTextNode(tab.title));
      // scp.js does the switching; this only records the choice and keeps
      // the placeholder box in step, which scp.js knows nothing about.
      a.addEventListener('click', function(){
        try { sessionStorage.setItem('bx-tab', tab.id); } catch(e) {}
        showPlaceholders(tab.id);
      });
      li.appendChild(a);
      ul.appendChild(li);
      items[tab.id] = li;
    });

    if (!order.length) return;
    setHidden(boot, true);

    /* ---- placeholder reference, pinned below the panels ---------- */
    var ph = by['bx_ph'];
    if (ph) root.appendChild(ph);

    function showPlaceholders(id){
      if (ph) setHidden(ph, CFG.phTabs.indexOf(id) === -1);
    }
    function selectTab(id){
      if (!panels[id]) id = order[0];
      order.forEach(function(k){
        panels[k].style.display = (k === id) ? '' : 'none';
        items[k].className = (k === id ? 'active' : '');
      });
      showPlaceholders(id);
    }

    /* ---- conditional visibility ---------------------------------- */
    var conds = CFG.conditions || {};
    function valueOf(name){
      var el = control(name);
      if (!el) return null;
      if (el.type === 'checkbox') return el.checked ? '1' : '0';
      return el.value;
    }
    function apply(){
      for (var target in conds) {
        var rule = conds[target],
            v = valueOf(rule.field),
            on = (v === null) || rule.in.indexOf(String(v)) !== -1;
        if (target.charAt(0) === '@') {
          // a whole group: its header bar and every setting under it
          var g = groups[target.substring(1)];
          if (!g) continue;
          setHidden(g.head, !on);
          g.fields.forEach(function(el){ setHidden(el, !on); });
        } else {
          setHidden(by[target], !on);
        }
      }
    }
    var watched = {};
    for (var t in conds) watched[conds[t].field] = true;
    for (var w in watched) {
      var ctl = control(w);
      if (!ctl) continue;
      ctl.addEventListener('change', apply);
      ctl.addEventListener('input', apply);
    }
    apply();

    /* ---- number-format presets fill in the detail fields --------- */
    var fmtCtl = control('number_format');
    if (fmtCtl && CFG.formats) {
      fmtCtl.addEventListener('change', function(){
        var preset = CFG.formats[fmtCtl.value];
        if (!preset) return;          // "custom" - leave what is there
        for (var key in preset) {
          var el = control(key);
          if (el) el.value = preset[key];
        }
      });
    }

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
    var bad = root.querySelector('.tab_content .form-field .error');
    if (bad && bad.closest) {
      var p = bad.closest('.tab_content');
      if (p) {
        for (var k in panels) if (panels[k] === p) start = k;
        // osTicket styles li.error with a red tab border of its own
        if (start && items[start]) items[start].className += ' error';
      }
    }
    if (!start) {
      try { start = sessionStorage.getItem('bx-tab'); } catch(e) {}
    }
    selectTab(start);
  }

  ready(function(){ try { build(); } catch (e) { if (window.console) console.warn('billing settings ui:', e); } });
})();
JS;
    }
}
