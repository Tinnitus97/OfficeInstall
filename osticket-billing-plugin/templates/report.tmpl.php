<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');
$base = ROOT_PATH.'scp/dispatcher.php/billing';

$qs = $_GET;
unset($qs['export']);
$mkUrl = function (array $extra) use ($base, $qs) {
    return $base.'/report?'.http_build_query(array_merge($qs, $extra));
};
$sortLink = function ($key, $label, $sortable) use ($filters, $mkUrl) {
    if (!$sortable)
        return Format::htmlchars($label);
    $dir = ($filters['sort'] === $key && $filters['dir'] !== 'asc') ? 'asc' : 'desc';
    $mark = '';
    if ($filters['sort'] === $key)
        $mark = $filters['dir'] === 'asc' ? ' &#9650;' : ' &#9660;';
    return '<a href="'.$mkUrl(array('sort' => $key, 'dir' => $dir)).'">'
        .Format::htmlchars($label).$mark.'</a>';
};
?>

<h2 class="billing-head"><span><?php echo $__('Report'); ?></span>
<?php $colFormAction = $mkUrl(array()); include __DIR__.'/coledit.tmpl.php'; ?></h2>

<form action="<?php echo $base; ?>/report" method="get" class="no-pjax billing-filters billing-panel" style="margin-bottom:1em;">
    <label><?php echo $__('Type'); ?>:</label>
    <select name="otype">
        <option value=""  <?php if ($filters['otype']==='')  echo 'selected'; ?>><?php echo $__('All'); ?></option>
        <option value="T" <?php if ($filters['otype']==='T') echo 'selected'; ?>><?php echo $__('Ticket'); ?></option>
        <option value="A" <?php if ($filters['otype']==='A') echo 'selected'; ?>><?php echo $__('Task'); ?></option>
    </select>
    &nbsp;<label><?php echo $__('Organization'); ?>:</label>
    <select name="org_id">
        <option value="0"><?php echo $__('All'); ?></option>
<?php   foreach ($orgs as $o) {
            $sel = ($filters['org_id'] == $o->getId()) ? ' selected' : '';
            echo '<option value="'.$o->getId().'"'.$sel.'>'.Format::htmlchars($o->getName()).'</option>';
        } ?>
    </select>
    &nbsp;<label><?php echo $__('Time type'); ?>:</label>
    <select name="time_type_id">
        <option value="0"><?php echo $__('All'); ?></option>
<?php   foreach ($timeTypes as $tid => $tt) {
            $sel = ($filters['time_type_id'] == $tid) ? ' selected' : '';
            echo '<option value="'.$tid.'"'.$sel.'>'.Format::htmlchars($tt->getName()).'</option>';
        } ?>
    </select>
    &nbsp;<label><?php echo $__('Period'); ?>:</label>
    <select name="range" onchange="this.form.submit();">
<?php foreach ($presets as $pk => $pl) { ?>
        <option value="<?php echo Format::htmlchars($pk); ?>"
          <?php echo (($filters['range'] ?? '') === $pk) ? 'selected' : ''; ?>><?php echo Format::htmlchars($pl); ?></option>
<?php } ?>
    </select>
    <label><?php echo $__('From'); ?>:</label>
    <input type="text" class="dp billing-date" autocomplete="off" size="12" style="display:inline-block;width:auto;"
           name="date_from" value="<?php echo Format::htmlchars($filters['date_from']); ?>">
    <label><?php echo $__('To'); ?>:</label>
    <input type="text" class="dp billing-date" autocomplete="off" size="12" style="display:inline-block;width:auto;"
           name="date_to" value="<?php echo Format::htmlchars($filters['date_to']); ?>">
    <input class="button" type="submit" value="<?php echo $__('Show'); ?>">
</form>

<?php if (!$rows) { ?>
    <p><em><?php echo $__('No time entries.'); ?></em></p>
<?php } else {
    $sumSecs = 0; $sumBill = 0; $sumAmount = 0.0;
    $canBulk = ($thisstaff->isAdmin() || ($config && $config->get('agent_access')))
        && !($config && $config->get('hide_open_items')); ?>
    <form action="<?php echo ROOT_PATH; ?>scp/dispatcher.php/billing/mark-billed" method="post" class="no-pjax">
    <?php csrf_token(); ?>
    <input type="hidden" name="return" value="<?php echo Format::htmlchars($mkUrl(array())); ?>">
    <table class="list" border="0" cellspacing="1" cellpadding="2" width="100%">
        <thead>
            <tr>
<?php       if ($canBulk) { ?>
                <th style="width:24px;">&nbsp;</th>
<?php       }
            foreach ($columns as $col) {
                $c = $col['key'];
                $style = 'width:'.(int) $col['width'].'px;';
                $style .= 'text-align:center;';
                echo '<th style="'.$style.'">'.$sortLink($c, $col['label'], $col['sortable']).'</th>';
            } ?>
            </tr>
        </thead>
        <tbody>
<?php   foreach ($rows as $r) {
            $isT  = ($r['object_type'] === 'T');
            $href = $base.'/'.($isT ? 'ticket' : 'task').'/'.(int) $r['object_id'];
            $sumSecs += $r['seconds']; $sumBill += (int)($r['billable_seconds'] ?? 0); $sumAmount += $r['amount']; ?>
            <tr>
<?php       if ($canBulk) {
                echo $isT
                    ? '<td style="text-align:center;"><input type="checkbox" name="ticket_ids[]" value="'.(int) $r['object_id'].'"></td>'
                    : '<td></td>';
            }
            foreach ($columns as $col) {
                if ($col['key'] === 'trips' && $canBulk && $r['object_type'] === 'T')
                    echo '<td style="text-align:center;"><input type="number" min="0" step="1" style="width:64px;"'
                       . ' name="ttrips['.(int) $r['object_id'].']" value="'.(int) ($r['trips'] ?? 0).'"></td>';
                else
                    echo billing_cell($col['key'], $r, $config, $__, $base);
            } ?>
            </tr>
<?php   } ?>
        </tbody>
        <tfoot><tr>
<?php   $bsum = Billing::summaryText($rows, $config, $__,
            isset($summaryExtra) ? $summaryExtra : array()); ?>
            <th colspan="<?php echo count($columns) + ($canBulk ? 1 : 0); ?>" style="text-align:left; font-weight:normal;">
                <b><?php echo Format::htmlchars($bsum['left']); ?></b>
                <span style="float:right;"><b><?php echo Format::htmlchars($bsum['right']); ?></b></span>
            </th>
        </tr></tfoot>
    </table>
<?php   $totals = Billing::totalsBlock($rows, $config, $__);
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
<?php       $hasTripsCol = false; foreach ($columns as $col) if ($col['key'] === 'trips') $hasTripsCol = true;
            if ($hasTripsCol) { ?>
        <button class="button" type="submit" name="do" value="save_trips" style="margin-left:1em;"><?php echo $__('Save trips'); ?></button>
<?php       } ?>
    </p>
<?php   } ?>
    </form>
    <p><?php echo $__('Page'); ?>: <b>[1]</b> &nbsp;
       <?php echo $__('Export'); ?>:
       <a class="no-pjax" href="<?php echo $mkUrl(array("export"=>"csv")); ?>">CSV</a> |
       <a class="no-pjax" href="<?php echo $mkUrl(array("export"=>"pdf")); ?>">PDF</a></p>
<?php } ?>

<?php
    // Same "section below the table" editor as the organization page — only
    // renders when the report is filtered to a single organization.
    $footerBase   = $base;
    $footerReturn = $mkUrl(array());
    include __DIR__.'/_footer_editor.tmpl.php';
?>

<p style="margin-top:1.5em;"><a href="<?php echo $base; ?>">&laquo; <?php echo $__('Back to Billing'); ?></a></p>
