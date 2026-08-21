<?php
if (!defined('OSTSCPINC') || !$thisstaff || !$thisstaff->isAdmin()) die('Access Denied');
$base = ROOT_PATH.'scp/dispatcher.php/billing';
?>
<h2><?php echo $__('Time types &amp; rates'); ?></h2>
<p><?php echo $__('Each time type maps a <code>time_type_id</code> from the recorded time entries to a name and an hourly rate. The default type (id 1) matches the entries created by the Time Recording plugin.'); ?></p>

<form action="<?php echo $base; ?>/timetypes" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
        <thead>
            <tr>
                <th style="width:60px;"><?php echo $__('ID'); ?></th>
                <th><?php echo $__('Name'); ?></th>
                <th style="width:130px;"><?php echo $__('Hourly rate'); ?></th>
                <th style="width:110px;"><?php echo $__('Factor'); ?> (%)</th>
                <th style="width:130px;"><?php echo $__('Travel flat fee'); ?></th>
                <th style="width:90px; text-align:center;"><?php echo $__('Billable'); ?></th>
                <th style="width:90px; text-align:center;"><?php echo $__('Active'); ?></th>
                <th style="width:90px; text-align:center;"><?php echo $__('Default'); ?></th>
                <th style="width:70px; text-align:center;"><?php echo $__('Delete'); ?></th>
            </tr>
        </thead>
        <tbody>
<?php       foreach ($types as $tid => $tt) { ?>
            <tr>
                <td><?php echo (int) $tid; ?><?php echo $tt->isDefault() ? ' <small>('.$__('default').')</small>' : ''; ?></td>
                <td><input type="text" name="tname[<?php echo (int) $tid; ?>]" value="<?php echo Format::htmlchars($tt->getName()); ?>" size="30"></td>
                <td><input type="text" name="rate[<?php echo (int) $tid; ?>]" value="<?php echo number_format($tt->getHourlyRate(), 2, '.', ''); ?>" size="10" inputmode="decimal"></td>
                <td><input type="number" name="tfactor[<?php echo (int) $tid; ?>]" value="<?php echo (int) $tt->getFactor(); ?>" min="1" max="1000" step="1" style="width:70px;"> %</td>
                <td><input type="text" name="ttravel[<?php echo (int) $tid; ?>]" value="<?php echo number_format($tt->getTravelFee(), 2, '.', ''); ?>" size="10" inputmode="decimal" title="<?php echo $__('Flat fee charged per trip'); ?>"></td>
                <td style="text-align:center;"><input type="checkbox" name="tbill[<?php echo (int) $tid; ?>]" value="1" <?php echo $tt->isBillable() ? 'checked' : ''; ?>></td>
                <td style="text-align:center;">
                    <input type="checkbox" name="tactive[<?php echo (int) $tid; ?>]" value="1" <?php echo $tt->isActive() ? 'checked' : ''; ?> <?php echo $tt->isDefault() ? 'disabled' : ''; ?>>
                </td>
                <td style="text-align:center;">
                    <input type="radio" name="default_type" value="<?php echo (int) $tid; ?>" <?php echo ((int) $currentDefault === (int) $tid) ? 'checked' : ''; ?>>
                </td>
                <td style="text-align:center;">
<?php               if ($tt->isDefault()) { ?>
                    <span class="faded" title="<?php echo $__('The default type cannot be deleted.'); ?>">&mdash;</span>
<?php               } else { ?>
                    <input type="checkbox" name="tdelete[<?php echo (int) $tid; ?>]" value="1">
<?php               } ?>
                </td>
            </tr>
<?php       } ?>
        </tbody>
    </table>
    <p>
        <button class="button" type="submit" name="do" value="save"
                onclick="var b=this.form.querySelectorAll('input[name^=&quot;tdelete&quot;]:checked'); if(b.length){return confirm('<?php echo $__('Save changes and delete the ticked time types? Deletion cannot be undone.'); ?>');} return true;">
            <?php echo $__('Save'); ?>
        </button>
        <small style="margin-left:1em; color:#666;"><?php echo $__('Saves all changes; rows ticked under "Delete" are removed.'); ?></small>
    </p>
</form>

<h2 style="margin-top:2em;"><?php echo $__('Add time type'); ?></h2>
<form action="<?php echo $base; ?>/timetypes" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <table class="form_table" width="100%" border="0" cellspacing="0" cellpadding="2">
        <tr><td width="180"><?php echo $__('Name'); ?>:</td>
            <td><input type="text" name="name" size="30"></td></tr>
        <tr><td><?php echo $__('Hourly rate'); ?>:</td>
            <td><input type="text" name="hourly_rate" value="0.00" size="10" inputmode="decimal"></td></tr>
        <tr><td><?php echo $__('Factor'); ?> (%):</td>
            <td><input type="number" name="factor" value="100" min="1" max="1000" step="1" style="width:80px;">
                <small><?php echo $__('e.g. 50/75/100/150/200 — billable time = time x factor'); ?></small></td></tr>
        <tr><td><?php echo $__('Travel flat fee'); ?>:</td>
            <td><input type="text" name="travel_fee" value="0.00" size="10" inputmode="decimal">
                <small><?php echo $__('Flat fee charged per trip (used with the per-ticket trip count).'); ?></small></td></tr>
        <tr><td><?php echo $__('Billable'); ?>:</td>
            <td><input type="checkbox" name="billable" value="1" checked></td></tr>
        <tr><td><?php echo $__('Sort'); ?>:</td>
            <td><input type="text" name="sort" value="0" size="5" inputmode="numeric"></td></tr>
    </table>
    <p><button class="button" type="submit" name="do" value="add"><?php echo $__('Add'); ?></button></p>
</form>

<p><a href="<?php echo $base; ?>">&laquo; <?php echo $__('Back to Billing'); ?></a></p>
