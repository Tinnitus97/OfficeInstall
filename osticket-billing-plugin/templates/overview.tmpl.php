<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');
$base = ROOT_PATH.'scp/dispatcher.php/billing';
?>


<?php if (!empty($searchError)) { ?>
    <div class="msg-warning"><?php echo $searchError; ?></div>
<?php } ?>

<div style="display:flex; gap:2rem; flex-wrap:wrap; margin-top:.5em;">

    <div style="flex:1; min-width:320px;">
        <h2><?php echo $__('Ticket invoice'); ?></h2>
        <p><?php echo $__('Open the time report and invoice for a single ticket.'); ?></p>
        <form action="<?php echo $base; ?>" method="get" class="no-pjax">
            <div class="billing-filters billing-panel">
                <label for="ticketno"><?php echo $__('Ticket number'); ?>:</label>
                <input type="text" id="ticketno" name="ticketno" size="18"
                       placeholder="<?php echo $__('e.g. 123456'); ?>">
                <input class="button" type="submit" value="<?php echo $__('Open'); ?>">
            </div>
        </form>
    </div>

    <div style="flex:1; min-width:320px;">
        <h2><?php echo $__('Organization billing'); ?></h2>
        <p><?php echo $__('Summarise all ticket time for an organization within a period.'); ?></p>
        <form action="<?php echo $base; ?>/org" method="get" class="no-pjax">
            <div class="billing-filters billing-panel">
                <label for="orgsel" style="flex:0 0 90px; display:inline-block;"><?php echo $__('Organization'); ?>:</label>
                <select name="id" id="orgsel">
                    <option value="">&mdash; <?php echo $__('select'); ?> &mdash;</option>
<?php           foreach ($orgs as $o) {
                    echo '<option value="'.$o->getId().'">'
                        .Format::htmlchars($o->getName()).'</option>';
                } ?>
                </select>
            </div>
            <div class="billing-filters billing-panel" style="margin-top:.6em;">
                <label style="flex:0 0 90px; display:inline-block;"><?php echo $__('Period'); ?>:</label>
                <select name="range">
<?php foreach (Billing::rangePresets($__) as $pk => $pl)
        echo '<option value="'.Format::htmlchars($pk).'"'
            .($pk === 'this_month' ? ' selected' : '').'>'
            .Format::htmlchars($pl).'</option>'; ?>
                </select>
                <label><?php echo $__('From'); ?>:</label>
                <input type="text" class="dp billing-date" autocomplete="off" size="12" style="display:inline-block;width:auto;"
                   name="start" value="<?php echo date('Y-m-01'); ?>">
                <label><?php echo $__('To'); ?>:</label>
                <input type="text" class="dp billing-date" autocomplete="off" size="12" style="display:inline-block;width:auto;"
                   name="end" value="<?php echo date('Y-m-t'); ?>">
            </div>
            <div style="margin-top:.8em;">
                <input class="button" type="submit" value="<?php echo $__('Show'); ?>">
            </div>
        </form>
    </div>
</div>

<h2 style="margin-top:1em;"><?php echo $__('Time &amp; Billing'); ?></h2>
<p><?php echo $__('Create billing reports and invoices from the times recorded on tickets and tasks.'); ?></p>
<div class="billing-filters billing-panel" style="margin:0.6em 0 1.2em;">
    <a class="button" href="<?php echo $base; ?>/report"><?php echo $__('Report'); ?> (CSV/PDF)</a>
    <a class="button" href="<?php echo $base; ?>/bulk-export"><?php echo $__('Bulk export'); ?></a>
<?php if ($thisstaff->isAdmin() && BillingPlugin::diagEnabled()) { ?>
    <a class="button" href="<?php echo $base; ?>/diag"><?php echo $__('Diagnostics'); ?></a>
<?php } ?>
</div>

<?php if (empty($hideOpenItems)) { ?>
<h2 class="billing-head" style="margin-top:1.2em;"><span><?php echo $__('Open items'); ?></span>
<?php $colFormAction = $base; include __DIR__.'/coledit.tmpl.php'; ?></h2>
<p><?php echo $__('All tickets and tasks that still have unsettled time entries.'); ?></p>
<?php if (empty($openRows)) { ?>
    <p><em><?php echo $__('No unsettled time entries found.'); ?></em></p>
<?php } else {
    $sumSecs = 0; $sumBill = 0; $sumAmount = 0.0;
    $canBulk = ($thisstaff->isAdmin() || ($config && $config->get('agent_access')))
        && !($config && $config->get('hide_open_items')); ?>
    <form action="<?php echo ROOT_PATH; ?>scp/dispatcher.php/billing/mark-billed" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <input type="hidden" name="return" value="<?php echo Format::htmlchars($base); ?>">
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
<?php   foreach ($openRows as $r) {
            $sumSecs += $r['seconds']; $sumBill += (int)($r['billable_seconds'] ?? 0); $sumAmount += $r['amount'];
            echo '<tr>';
            if ($canBulk) {
                echo ($r['object_type'] === 'T')
                    ? '<td style="text-align:center;"><input type="checkbox" name="ticket_ids[]" value="'.(int) $r['object_id'].'"></td>'
                    : '<td></td>';
            }
            foreach ($columns as $col)
                echo billing_cell($col['key'], $r, $config, $__, $base);
            echo '</tr>';
        } ?>
        </tbody>
        <tfoot><tr>
<?php   $bsum = Billing::summaryText($openRows, $config, $__,
            isset($summaryExtra) ? $summaryExtra : array()); ?>
            <th colspan="<?php echo count($columns) + ($canBulk ? 1 : 0); ?>" style="text-align:left; font-weight:normal;">
                <b><?php echo Format::htmlchars($bsum['left']); ?></b>
                <span style="float:right;"><b><?php echo Format::htmlchars($bsum['right']); ?></b></span>
            </th>
        </tr></tfoot>
    </table>
<?php   $totals = Billing::totalsBlock($openRows, $config, $__);
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
    <p><?php echo $__('Page'); ?>: <b>[1]</b> &nbsp;
       <?php echo $__('Export'); ?>:
       <a class="no-pjax" href="<?php echo $base; ?>?export=csv">CSV</a> |
       <a class="no-pjax" href="<?php echo $base; ?>?export=pdf">PDF</a></p>
<?php } ?>
<?php } /* hide_open_items */ ?>
