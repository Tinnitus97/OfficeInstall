<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');
$base = ROOT_PATH.'scp/dispatcher.php/billing';
?>
<h2><?php echo $__('Bulk export'); ?></h2>
<p><?php echo $__('Select any number of organizations and download one ZIP archive containing a document per organization. The file names inside follow the configured export file name pattern.'); ?></p>

<?php if (!empty($notice)) { ?>
<div class="billing-panel" style="border-left:3px solid #c00; padding:.6em 1em; margin:.8em 0;">
    <?php echo Format::htmlchars($notice); ?>
</div>
<?php } ?>

<form action="<?php echo $base; ?>/bulk-export" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <div class="billing-filters billing-panel" style="margin:.6em 0 1.2em; padding:1em;">
        <table width="100%" cellpadding="4" cellspacing="0">
            <tr>
                <td style="width:150px; vertical-align:top;"><b><?php echo $__('Organizations'); ?>:</b></td>
                <td>
                    <select id="billing-bulk-orgs" name="orgs[]" multiple="multiple" style="width:100%;"
                            data-placeholder="<?php echo $__('Search and select organizations'); ?>">
<?php   foreach ($orgs as $o) {
            $oid = (int) $o->getId(); ?>
                        <option value="<?php echo $oid; ?>" <?php echo in_array($oid, $selected) ? 'selected' : ''; ?>>
                            <?php echo Format::htmlchars($o->getName()); ?>
                        </option>
<?php   } ?>
                    </select>
                    <small style="color:#666;"><?php echo $__('The selection is remembered for your next visit.'); ?></small>
                </td>
            </tr>
            <tr>
                <td><b><?php echo $__('Period'); ?>:</b></td>
                <td>
                    <select name="range" onchange="this.form.date_from.disabled=this.form.date_to.disabled=(this.value!='');">
<?php   foreach ($presets as $k => $label) { ?>
                        <option value="<?php echo Format::htmlchars($k); ?>" <?php echo ($range === $k) ? 'selected' : ''; ?>>
                            <?php echo Format::htmlchars($label); ?></option>
<?php   } ?>
                    </select>
                    &nbsp; <?php echo $__('From'); ?>:
                    <input type="date" name="date_from" value="<?php echo Format::htmlchars($start); ?>">
                    <?php echo $__('To'); ?>:
                    <input type="date" name="date_to" value="<?php echo Format::htmlchars($end); ?>">
                </td>
            </tr>
            <tr>
                <td><b><?php echo $__('Format'); ?>:</b></td>
                <td>
                    <label><input type="radio" name="format" value="pdf"  <?php echo $format === 'pdf'  ? 'checked' : ''; ?>> PDF</label>
                    &nbsp;<label><input type="radio" name="format" value="csv"  <?php echo $format === 'csv'  ? 'checked' : ''; ?>> CSV</label>
                    &nbsp;<label><input type="radio" name="format" value="both" <?php echo $format === 'both' ? 'checked' : ''; ?>> <?php echo $__('Both'); ?></label>
                </td>
            </tr>
        </table>
        <p style="margin:.8em 0 0;">
            <button class="green button" type="submit" name="do" value="export"><?php echo $__('Export as ZIP'); ?></button>
            <button class="button" type="submit" name="do" value="save"><?php echo $__('Save selection'); ?></button>
        </p>
        <p style="margin:.4em 0 0; color:#666;"><small><?php
            echo $__('Organizations without billable entries in the period are skipped.'); ?></small></p>
    </div>
</form>

<script type="text/javascript">
// osTicket ships select2; use it when present so the list stays searchable
// even with many organizations, and degrade to a plain multi-select if not.
(function () {
    function init() {
        if (window.jQuery && jQuery.fn && jQuery.fn.select2) {
            jQuery('#billing-bulk-orgs').select2({
                width: '100%',
                placeholder: jQuery('#billing-bulk-orgs').data('placeholder'),
                allowClear: true
            });
        } else {
            var el = document.getElementById('billing-bulk-orgs');
            if (el) el.size = 10;   // usable fallback without select2
        }
    }
    if (window.jQuery) jQuery(init); else document.addEventListener('DOMContentLoaded', init);
})();
</script>

<p style="margin-top:1.5em;"><a href="<?php echo $base; ?>">&laquo; <?php echo $__('Back to Billing'); ?></a></p>
