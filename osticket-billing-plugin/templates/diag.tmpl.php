<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');
$base = ROOT_PATH.'scp/dispatcher.php/billing';
?>

<h2><?php echo $__('Diagnostics'); ?></h2>
<p><?php echo $__('Time spent inside the handlers of this plugin, compared with the total request duration. Create a ticket, then reload this page.'); ?></p>

<div class="billing-panel" style="margin:.6em 0 1.2em;">
    <b><?php echo $__('Timesheet index'); ?>:</b>
<?php if ($hasIndex) { ?>
    <span style="color:#2a7;"><?php echo $__('present'); ?></span>
    &mdash; <?php echo $__('billing queries use the index.'); ?>
<?php } else { ?>
    <span style="color:#c33;"><?php echo $__('MISSING'); ?></span>
    &mdash; <?php echo $__('every billing query scans the whole table. This slows down saving.'); ?>
<?php } ?>
</div>

<?php if (!$perfRows) { ?>
    <p><em><?php echo $__('No measurements recorded yet in this session. Create or update a ticket and come back.'); ?></em></p>
<?php } else { ?>
    <table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
        <thead>
            <tr>
                <th style="text-align:center;"><?php echo $__('Time'); ?></th>
                <th style="text-align:center;"><?php echo $__('Handler'); ?></th>
                <th style="text-align:center;"><?php echo $__('Plugin'); ?> (ms)</th>
                <th style="text-align:center;"><?php echo $__('Whole request'); ?> (ms)</th>
                <th style="text-align:center;"><?php echo $__('Plugin share'); ?></th>
                <th style="text-align:center;"><?php echo $__('Request'); ?></th>
            </tr>
        </thead>
        <tbody>
<?php   foreach ($perfRows as $r) {
            $share = ($r['req_ms'] > 0) ? ($r['ms'] / $r['req_ms'] * 100) : 0;
            $warn  = ($r['ms'] > 250);
?>
            <tr<?php echo $warn ? ' style="background:#fff3f3;"' : ''; ?>>
                <td style="text-align:center;"><?php echo Format::htmlchars($r['when']); ?></td>
                <td style="text-align:center;"><?php echo Format::htmlchars($r['key']); ?></td>
                <td style="text-align:center;"><b><?php echo number_format($r['ms'], 1); ?></b></td>
                <td style="text-align:center;"><?php echo number_format($r['req_ms'], 1); ?></td>
                <td style="text-align:center;"><?php echo number_format($share, 1); ?> %</td>
                <td style="text-align:center;"><?php echo Format::htmlchars($r['method'].' '.$r['uri']); ?></td>
            </tr>
<?php   } ?>
        </tbody>
    </table>
    <p style="margin-top:.8em;">
        <a class="button" href="<?php echo $base; ?>/diag?clear=1"><?php echo $__('Clear measurements'); ?></a>
    </p>
    <p><em><?php echo $__('If the plugin share is small while the whole request is slow, the delay is caused elsewhere (another plugin, the web server or the database).'); ?></em></p>
<?php } ?>

<p style="margin-top:1.5em;"><a href="<?php echo $base; ?>">&laquo; <?php echo $__('Back to Billing'); ?></a></p>
