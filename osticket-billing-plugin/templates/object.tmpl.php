<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');

$isTicket = ($object_type === 'T');
$number   = method_exists($object, 'getNumber')  ? $object->getNumber()  : $object_id;
$subject  = method_exists($object, 'getSubject') ? $object->getSubject() : '';
$objLink  = ROOT_PATH.'scp/'.($isTicket ? 'tickets.php?id=' : 'tasks.php?id=').$object_id;
$selfUrl  = ROOT_PATH.'scp/dispatcher.php/billing/'.($isTicket ? 'ticket' : 'task').'/'.$object_id;
$canSettle = $thisstaff->isAdmin() || ($config && $config->get('agent_access'));
?>
<h2><?php echo $__('Time &amp; Billing'); ?>
    &mdash; <?php echo $isTicket ? $__('Ticket') : $__('Task'); ?>
    <a href="<?php echo $objLink; ?>">#<?php echo Format::htmlchars($number); ?></a>
</h2>
<?php if ($subject) { ?>
    <p><b><?php echo $__('Subject'); ?>:</b> <?php echo Format::htmlchars($subject); ?></p>
<?php } ?>
<p><b><?php echo $__('Generated'); ?>:</b> <?php echo Format::datetime(Misc::gmtime(), false); ?></p>

<h2><?php echo $__('Invoice summary'); ?></h2>
<table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
    <thead>
        <tr>
            <th><?php echo $__('Time type'); ?></th>
            <th style="text-align:center;"><?php echo $__('Time'); ?></th>
            <th style="text-align:center;"><?php echo $__('Factor'); ?></th>
            <th style="text-align:center;"><?php echo $__('Billable time'); ?></th>
            <th style="text-align:center;"><?php echo $__('Hours'); ?></th>
            <th style="text-align:center;"><?php echo $__('Rate'); ?></th>
            <th style="text-align:center;"><?php echo $__('Amount'); ?></th>
            <th style="text-align:center;"><?php echo $__('Billable'); ?></th>
        </tr>
    </thead>
    <tbody>
<?php   if (!$invoice['lines']) { ?>
        <tr><td colspan="8"><em><?php echo $__('No time recorded.'); ?></em></td></tr>
<?php   } else {
            foreach ($invoice['lines'] as $ln) { ?>
        <tr>
            <td><?php echo Format::htmlchars(Billing::typeLabel($ln['name'], $ln['factor'] ?? 100)); ?></td>
            <td style="text-align:center;"><?php echo Billing::formatDuration($ln['seconds']); ?></td>
            <td style="text-align:center;"><?php echo (int) ($ln['factor'] ?? 100); ?> %</td>
            <td style="text-align:center;"><?php echo Billing::formatDuration($ln['billed_seconds'] ?? $ln['seconds']); ?></td>
            <td style="text-align:center;"><?php echo number_format($ln['hours'], 2); ?></td>
            <td style="text-align:center;"><?php echo Billing::formatMoney($ln['rate'], $config); ?></td>
            <td style="text-align:center;"><?php echo $ln['billable'] ? Billing::formatMoney($ln['amount'], $config) : '&mdash;'; ?></td>
            <td style="text-align:center;"><?php echo $ln['billable'] ? $__('Yes') : $__('No'); ?></td>
        </tr>
<?php       }
        } ?>
    </tbody>
<?php if ($invoice['lines']) {
    $moneyMode = !($config && $config->get('billing_mode') === 'time'); ?>
    <tfoot>
<?php   $trips  = (int) ($invoice['trips'] ?? 0);
        $travel = (float) ($invoice['travel_amount'] ?? 0);
        if ($moneyMode) { ?>
<?php       if (!empty($invoice['surcharge_amount']) || $travel > 0) { ?>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo $__('Base amount'); ?></td>
            <td style="text-align:right;"><?php echo Billing::formatMoney($invoice['subtotal'] - $invoice['surcharge_amount'] - $travel, $config); ?></td>
            <td></td>
        </tr>
<?php           if (!empty($invoice['surcharge_amount'])) { ?>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo $__('Surcharges for special hours'); ?></td>
            <td style="text-align:right;">+<?php echo Billing::formatMoney($invoice['surcharge_amount'], $config); ?></td>
            <td></td>
        </tr>
<?php           } ?>
<?php           if ($travel > 0) { ?>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo sprintf($__('Travel charges (%d)'), $trips); ?>
                <small>(<?php echo $trips; ?> &times; <?php echo Billing::formatMoney($invoice['travel_fee'], $config); ?>)</small></td>
            <td style="text-align:right;">+<?php echo Billing::formatMoney($travel, $config); ?></td>
            <td></td>
        </tr>
<?php           } ?>
<?php       } ?>
        <tr>
            <td colspan="6" style="text-align:right;"><b><?php echo $__('Subtotal'); ?></b></td>
            <td style="text-align:right;"><b><?php echo Billing::formatMoney($invoice['subtotal'], $config); ?></b></td>
            <td></td>
        </tr>
<?php       if ($invoice['tax_rate'] > 0) { ?>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo sprintf($__('Tax (%s%%)'), rtrim(rtrim(number_format($invoice['tax_rate'],2),'0'),'.')); ?></td>
            <td style="text-align:right;"><?php echo Billing::formatMoney($invoice['tax'], $config); ?></td>
            <td></td>
        </tr>
<?php       } ?>
        <tr>
            <td colspan="6" style="text-align:right;"><b><?php echo $__('Total'); ?></b></td>
            <td style="text-align:right;"><b><?php echo Billing::formatMoney($invoice['total'], $config); ?></b></td>
            <td></td>
        </tr>
<?php   } else { ?>
<?php       if (!empty($invoice['surcharge_seconds'])) { ?>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo $__('Total time'); ?></td>
            <td style="text-align:right;"><?php echo Billing::formatDuration($invoice['total_seconds']); ?></td>
            <td></td>
        </tr>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo $__('Surcharges for special hours'); ?></td>
            <td style="text-align:right;">+<?php echo Billing::formatDuration($invoice['surcharge_seconds']); ?></td>
            <td></td>
        </tr>
<?php       } ?>
<?php       if ($trips > 0) { ?>
        <tr>
            <td colspan="6" style="text-align:right;"><?php echo $__('Trips'); ?></td>
            <td style="text-align:right;"><?php echo $trips; ?></td>
            <td></td>
        </tr>
<?php       } ?>
        <tr>
            <td colspan="6" style="text-align:right;"><b><?php echo $__('Billable time'); ?></b></td>
            <td style="text-align:right;"><b><?php echo Billing::formatDuration($invoice['billable_seconds'] ?? 0); ?></b></td>
            <td></td>
        </tr>
<?php   }
        if (!empty($invoice['is_goodwill'])) { ?>
        <tr>
            <td colspan="8" style="text-align:right; color:#a55;">
                <b><?php echo $__('Goodwill (Kulanz)'); ?></b>
                &mdash; <?php echo $__('this ticket is not being invoiced.'); ?>
            </td>
        </tr>
<?php   } ?>
    </tfoot>
<?php } ?>
</table>

<h2 style="margin-top:1.5em;"><?php echo $__('Time entries'); ?></h2>
<p><em><?php echo $__('The ticket is billed as a whole. The entries below are shown for information only.'); ?></em></p>
<form action="<?php echo $selfUrl; ?>" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
        <thead>
            <tr>
                <th><?php echo $__('Date'); ?></th>
                <th><?php echo $__('Type'); ?></th>
                <th><?php echo $__('Agent'); ?></th>
                <th><?php echo $__('Post'); ?></th>
                <th style="text-align:center;"><?php echo $__('Time'); ?></th>
                <th><?php echo $__('Time type'); ?></th>
            </tr>
        </thead>
        <tbody>
<?php       if (!$entries) { ?>
            <tr><td colspan="6"><em><?php echo $__('No time entries.'); ?></em></td></tr>
<?php       } else {
                foreach ($entries as $e) {
                    $tid  = (int) $e['time_type_id'];
                    $tname = isset($types[$tid])
                        ? Billing::typeLabel($types[$tid]->getName(), $types[$tid]->getFactor())
                        : sprintf($__('Type %d'), $tid);
                    $ptype = ($e['entry_type'] === 'N') ? $__('Note')
                           : (($e['entry_type'] === 'R') ? $__('Response') : $e['entry_type']);
?>
            <tr>
                <td><?php echo Format::datetime($e['created']); ?></td>
                <td><?php echo Format::htmlchars($ptype); ?></td>
                <td><?php echo Format::htmlchars($e['staff_name'] ?: '&mdash;'); ?></td>
                <td><?php echo Format::htmlchars($e['title'] ?: ($e['poster'] ?: '')); ?></td>
                <td style="text-align:right;"><?php echo Billing::formatDuration($e['secs'], true); ?></td>
                <td><?php echo Format::htmlchars($tname); ?></td>
            </tr>
<?php           }
            } ?>
        </tbody>
    </table>
</form>

<?php if ($object_type === 'T' && $canBulk) { ?>
<div class="billing-panel" style="margin-top:1em;">
<?php if (!empty($isGoodwill)) { ?>
    <span style="color:#a55;"><b><?php echo $__('Goodwill (Kulanz)'); ?></b>
        &mdash; <?php echo $__('this ticket is not being invoiced.'); ?></span><br>
<?php } ?>
    <form action="<?php echo $selfUrl; ?>" method="post" class="no-pjax" style="display:inline-block; margin-top:.4em;">
        <?php csrf_token(); ?>
        <input type="hidden" name="do" value="toggle_goodwill">
        <button class="button" type="submit">
            <?php echo !empty($isGoodwill) ? $__('Remove goodwill flag') : $__('Mark as goodwill (Kulanz)'); ?>
        </button>
    </form>
    &nbsp;
    <form action="<?php echo ROOT_PATH; ?>scp/dispatcher.php/billing/mark-billed" method="post" class="no-pjax" style="display:inline-block;">
        <?php csrf_token(); ?>
        <input type="hidden" name="ticket_id" value="<?php echo (int) $object_id; ?>">
        <input type="hidden" name="return" value="<?php echo Format::htmlchars($selfUrl); ?>">
        <input type="hidden" name="billed" value="<?php echo !empty($isBilled) ? '0' : '1'; ?>">
        <button class="button" type="submit">
            <?php echo !empty($isBilled) ? $__('Mark as open') : $__('Mark as billed'); ?>
        </button>
    </form>
<?php if (!empty($isBilled)) { ?>
    <span style="color:#5a5; margin-left:.6em;"><b><?php echo $__('Billed'); ?></b></span>
<?php } ?>
</div>
<?php } ?>

<p><a href="<?php echo ROOT_PATH; ?>scp/dispatcher.php/billing">&laquo; <?php echo $__('Back to Billing'); ?></a></p>
