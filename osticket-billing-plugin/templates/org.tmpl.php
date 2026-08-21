<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');
$base = ROOT_PATH.'scp/dispatcher.php/billing';
?>

<h2><?php echo $__('Organization billing'); ?></h2>

<form action="<?php echo $base; ?>/org" method="get" class="no-pjax billing-filters billing-panel" style="margin-bottom:1.5em;">
    <label for="orgsel"><?php echo $__('Organization'); ?>:</label>
    <select name="id" id="orgsel">
        <option value="">&mdash; <?php echo $__('select'); ?> &mdash;</option>
<?php   foreach ($orgs as $o) {
            $sel = ($org && $o->getId() == $org->getId()) ? ' selected' : '';
            echo '<option value="'.$o->getId().'"'.$sel.'>'.Format::htmlchars($o->getName()).'</option>';
        } ?>
    </select>
    &nbsp;<label><?php echo $__('Period'); ?>:</label>
    <select name="range" onchange="this.form.submit();">
<?php foreach (($presets ?? array()) as $pk => $pl) { ?>
        <option value="<?php echo Format::htmlchars($pk); ?>"
          <?php echo ((isset($range) ? $range : '') === $pk) ? 'selected' : ''; ?>><?php echo Format::htmlchars($pl); ?></option>
<?php } ?>
    </select>
    <label><?php echo $__('From'); ?>:</label>
    <input type="text" class="dp billing-date" autocomplete="off" size="12" style="display:inline-block;width:auto;"
           name="start" value="<?php echo Format::htmlchars($start); ?>">
    <label><?php echo $__('To'); ?>:</label>
    <input type="text" class="dp billing-date" autocomplete="off" size="12" style="display:inline-block;width:auto;"
           name="end" value="<?php echo Format::htmlchars($end); ?>">
    <input class="button" type="submit" value="<?php echo $__('Show'); ?>">
</form>

<?php if ($org && $report !== null) {
    $expQ = http_build_query(array('id'=>$org->getId(),'start'=>$start,'end'=>$end)); ?>
    <h2 class="billing-head"><span><?php echo Format::htmlchars($org->getName()); ?>
        <small>(<?php echo Format::htmlchars($start); ?> &ndash; <?php echo Format::htmlchars($end); ?>)</small></span>
        <?php $colFormAction = $base.'/org?'.$expQ; include __DIR__.'/coledit.tmpl.php'; ?>
    </h2>
<?php if (empty($orgRows)) { ?>
    <p><em><?php echo $__('No billable time found for this organization and period.'); ?></em></p>
<?php } else {
    $sumSecs = 0; $sumBill = 0; $sumAmount = 0.0;
    $canBulk = ($thisstaff->isAdmin() || ($config && $config->get('agent_access')))
        && !($config && $config->get('hide_open_items')); ?>
    <form action="<?php echo ROOT_PATH; ?>scp/dispatcher.php/billing/mark-billed" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <input type="hidden" name="return" value="<?php echo Format::htmlchars($base.'/org?'.$expQ); ?>">
    <table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
        <thead><tr>
<?php   if ($canBulk) { ?>
            <th style="width:24px;">&nbsp;</th>
<?php   }
        foreach ($columns as $col) {
            $style = 'width:'.(int) $col['width'].'px;';
            $style .= 'text-align:center;';
            echo '<th style="'.$style.'">'.Format::htmlchars($col['label']).'</th>';
        } ?>
        </tr></thead>
        <tbody>
<?php   foreach ($orgRows as $r) {
            $sumSecs += $r['seconds']; $sumBill += (int)($r['billable_seconds'] ?? 0); $sumAmount += $r['amount'];
            echo '<tr>';
            if ($canBulk)
                echo '<td style="text-align:center;"><input type="checkbox" name="ticket_ids[]" value="'.(int) $r['object_id'].'"></td>';
            foreach ($columns as $col)
                echo billing_cell($col['key'], $r, $config, $__, $base);
            echo '</tr>';
        } ?>
        </tbody>
        <tfoot><tr>
<?php   $bsum = Billing::summaryText($orgRows, $config, $__,
            isset($summaryExtra) ? $summaryExtra : array()); ?>
            <th colspan="<?php echo count($columns) + ($canBulk ? 1 : 0); ?>" style="text-align:left; font-weight:normal;">
                <b><?php echo Format::htmlchars($bsum['left']); ?></b>
                <span style="float:right;"><b><?php echo Format::htmlchars($bsum['right']); ?></b></span>
            </th>
        </tr></tfoot>
    </table>
<?php   $totals = Billing::totalsBlock($orgRows, $config, $__);
        if ($totals) { ?>
    <div class="billing-totals" style="margin:.8em 0; text-align:right;">
<?php       $i = 0; $n = count($totals);
            foreach ($totals as $tLabel => $tValue) { $i++; ?>
        <span style="margin-left:1.6em;<?php echo ($i === $n) ? 'font-weight:700;' : ''; ?>">
            <?php echo Format::htmlchars($tLabel); ?>:
            <?php echo Format::htmlchars($tValue); ?>
        </span>
<?php       } ?>
    </div>
<?php   } ?>
<?php   if ($canBulk) { ?>
    <p style="margin-top:.6em;">
        <span style="margin-right:1em;"><?php echo $__('Select'); ?>:
            <a href="#" onclick="billingSel(this,1);return false;"><?php echo $__('All'); ?></a> &nbsp;
            <a href="#" onclick="billingSel(this,0);return false;"><?php echo $__('None'); ?></a> &nbsp;
            <a href="#" onclick="billingSel(this,-1);return false;"><?php echo $__('Toggle'); ?></a>
        </span>
        <button class="green button" type="submit" name="billed" value="1"><?php echo $__('Mark selected as billed'); ?></button>
        <button class="button" type="submit" name="billed" value="0"><?php echo $__('Mark selected as open'); ?></button>
    </p>
<?php   } ?>
    </form>
<?php   } ?>
    <p style="margin-top:.4em;"><?php echo $__('Export'); ?>:
       <a class="no-pjax" href="<?php echo $base; ?>/org?<?php echo $expQ; ?>&export=csv">CSV</a> |
       <a class="no-pjax" href="<?php echo $base; ?>/org?<?php echo $expQ; ?>&export=pdf">PDF</a></p>
    <?php
        $footerOrg    = $org;
        $footerBase   = $base;
        $footerReturn = $base.'/org?'.$expQ;
        include __DIR__.'/_footer_editor.tmpl.php';
    ?>
<?php } ?>
<p style="margin-top:1.5em;"><a href="<?php echo $base; ?>">&laquo; <?php echo $__('Back to Billing'); ?></a></p>
