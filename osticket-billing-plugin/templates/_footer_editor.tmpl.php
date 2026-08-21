<?php
// Shared editor for the per-organization "section below the table"
// (off / customer note / free-text table). Included by org.tmpl.php and
// report.tmpl.php so both pages show and edit exactly what the export prints.
//
// Expected data variables (as passed to the page):
//   $footerMode, $orgNote, $noteDefault, $orgTable, $tableTitle,
//   $tableMetaLine, $tableCols, $tableRows
// Plus, set by the including template:
//   $footerOrg    = Organization object (nothing renders without it)
//   $footerBase   = base dispatcher URL for the billing app
//   $footerReturn = URL to return to after saving
if (!empty($footerOrg)) {
    $fm = isset($footerMode) ? $footerMode : 'note';
    if ($fm === 'note') {
        $noteData = isset($orgNote) ? $orgNote : null;
        $usingDefault = false;
        if ($noteData) {
            $noteText = (string) $noteData['note'];
        } else {
            // no note of its own yet -> start from the configured default
            $noteText = isset($noteDefault) ? (string) $noteDefault : '';
            $usingDefault = ($noteText !== '');
        } ?>
    <div class="billing-panel" style="margin-top:1.8em;padding:1em;border:1px solid #ddd;background:#fafafa;">
        <h3 style="margin:0 0 .5em;"><?php echo $__('Customer note'); ?>
            <small style="font-weight:normal;color:#666;">&mdash; <?php echo Format::htmlchars($footerOrg->getName()); ?></small></h3>
        <form action="<?php echo $footerBase; ?>/org-note" method="post" class="no-pjax">
            <?php csrf_token(); ?>
            <input type="hidden" name="do" value="save_note">
            <input type="hidden" name="org_id" value="<?php echo (int) $footerOrg->getId(); ?>">
            <input type="hidden" name="return" value="<?php echo Format::htmlchars($footerReturn); ?>">
            <textarea name="note" rows="7" style="width:100%;box-sizing:border-box;"
                placeholder="<?php echo $__('Enter a note for this organization. It is saved per organization and can be overwritten at any time.'); ?>"><?php
                echo Format::htmlchars($noteText); ?></textarea>
            <p style="margin:.6em 0 0;">
                <button class="green button" type="submit"><?php echo $__('Save note'); ?></button>
<?php   if ($noteData) { ?>
                <small style="margin-left:1em;color:#666;">
                    <?php echo $__('Last updated'); ?>: <?php echo Format::htmlchars(Billing::formatDateTime($noteData['updated'])); ?><?php
                    if (!empty($noteData['updated_by'])) echo ' &mdash; '.Format::htmlchars($noteData['updated_by']); ?>
                </small>
<?php   } elseif ($usingDefault) { ?>
                <small style="margin-left:1em;color:#666;"><?php echo $__('Prefilled with the default text — save to keep it for this organization.'); ?></small>
<?php   } ?>
            </p>
            <p style="margin:.4em 0 0;color:#666;"><small><?php
                echo $__('This note appears under the table in the CSV and PDF export for this organization. Clear the field and save to remove it.'); ?></small></p>
        </form>
    </div>
<?php } elseif ($fm === 'checks') {
        $tblData = (isset($orgTable) && is_array($orgTable)) ? $orgTable : array('rows'=>array(),'updated'=>'','updated_by'=>'');
        $store   = (isset($tblData['rows']) && is_array($tblData['rows'])) ? $tblData['rows'] : array();
        $cols    = (isset($tableCols) && is_array($tableCols)) ? $tableCols : array('name'=>'','data'=>array());
        $rdefs   = (isset($tableRows) && is_array($tableRows)) ? $tableRows : array();
        $dataCols = isset($cols['data']) ? $cols['data'] : array();
        $ttl      = isset($tableTitle) ? (string) $tableTitle : '';
        $metaLine = isset($tableMetaLine) ? (string) $tableMetaLine : ''; ?>
    <div class="billing-panel" style="margin-top:1.8em;padding:1em;border:1px solid #ddd;background:#fafafa;">
        <h3 style="margin:0 0 .3em;"><?php echo $ttl !== '' ? Format::htmlchars($ttl) : $__('Table'); ?>
            <small style="font-weight:normal;color:#666;">&mdash; <?php echo Format::htmlchars($footerOrg->getName()); ?></small></h3>
<?php   if ($metaLine !== '') { ?>
        <p style="margin:0 0 .7em;"><strong><?php echo Format::htmlchars($metaLine); ?></strong></p>
<?php   } ?>
        <form action="<?php echo $footerBase; ?>/org-checks" method="post" class="no-pjax" autocomplete="off">
            <?php csrf_token(); ?>
            <input type="hidden" name="do" value="save_checks">
            <input type="hidden" name="org_id" value="<?php echo (int) $footerOrg->getId(); ?>">
            <input type="hidden" name="return" value="<?php echo Format::htmlchars($footerReturn); ?>">
            <table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
                <thead><tr>
                    <th style="width:1%;white-space:nowrap;text-align:center;"><?php echo $__('Active'); ?></th>
                    <th style="text-align:left;"><?php echo Format::htmlchars($cols['name']); ?></th>
<?php       foreach ($dataCols as $c) { ?>
                    <th><?php echo Format::htmlchars($c['label']); ?></th>
<?php       } ?>
                </tr></thead>
                <tbody>
<?php       foreach ($rdefs as $rdef) {
                $rk = $rdef['key'];
                $st = (isset($store[$rk]) && is_array($store[$rk])) ? $store[$rk] : array();
                $cells = (isset($st['cells']) && is_array($st['cells'])) ? $st['cells'] : array();
                $isOn  = !isset($st['active']) ? true : (bool) $st['active']; ?>
                    <tr>
                        <td style="text-align:center;">
                            <input type="checkbox" autocomplete="off" name="tbl[<?php echo $rk; ?>][active]" value="1" <?php echo $isOn ? 'checked' : ''; ?>>
                            <input type="hidden" name="tbl[<?php echo $rk; ?>][present]" value="1">
                        </td>
                        <td style="text-align:left;"><b><?php echo Format::htmlchars($rdef['label']); ?></b></td>
<?php           foreach ($dataCols as $c) { ?>
                        <td><input type="text" autocomplete="off" style="width:100%;box-sizing:border-box;"
                                   name="tbl[<?php echo $rk; ?>][cells][<?php echo $c['key']; ?>]"
                                   value="<?php echo Format::htmlchars(isset($cells[$c['key']]) ? $cells[$c['key']] : ''); ?>"></td>
<?php           } ?>
                    </tr>
<?php       } ?>
                </tbody>
            </table>
            <p style="margin:.6em 0 0;">
                <button class="green button" type="submit"><?php echo $__('Save table'); ?></button>
            </p>
            <p style="margin:.4em 0 0;color:#666;"><small><?php
                echo $__('This table appears under the report table in the CSV and PDF export for this organization. Rows whose "Active" box is unticked are not exported.'); ?></small></p>
        </form>
    </div>
<?php }
} // if !empty($footerOrg)
