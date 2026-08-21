<?php
if (!defined('OSTSCPINC') || !$thisstaff) die('Access Denied');
/* Shared partial: normalized-row cell renderer + the queue-style column
   editor. Include AFTER setting: $columns,$catalog,$addable,$canManage,
   $notice,$colFormAction,$__,$config. */
if (!function_exists('billing_cell')) {
    function billing_cell($key, $r, $config, $__, $base) {
        $isT  = ($r['object_type'] === 'T');
        $href = $base.'/'.($isT ? 'ticket' : 'task').'/'.(int) $r['object_id'];
        switch ($key) {
            case 'created': return '<td style="text-align:center;">'.Format::htmlchars(Billing::formatDate($r['created'] ?? '')).'</td>';
            case 'number':  return '<td style="text-align:center;"><a href="'.$href.'">#'.Format::htmlchars($r['number']).'</a></td>';
            case 'subject': return '<td style="text-align:center;">'.Format::htmlchars($r['subject']).'</td>';
            case 'org':     return '<td style="text-align:center;">'.Format::htmlchars($r['org']).'</td>';
            case 'agent':   return '<td style="text-align:center;">'.Format::htmlchars($r['agent']).'</td>';
            case 'type':    return '<td style="text-align:center;">'.Format::htmlchars(Billing::typeLabel($r['type_name'], $r['factor'] ?? 100))
                                .(!empty($r['billable']) ? '' : ' <small>('.$__('non-billable').')</small>').'</td>';
            case 'time':    return '<td style="text-align:center;">'.Billing::formatDuration($r['seconds']).'</td>';
            case 'billable_time': return '<td style="text-align:center;">'.Billing::formatDuration((int)($r['billable_seconds'] ?? 0)).'</td>';
            case 'factor':  return '<td style="text-align:center;">'.(isset($r['factor']) ? ((int)$r['factor']).'%' : '').'</td>';
            case 'onsite':  return '<td style="text-align:center;">'.(!empty($r['onsite'])
                                ? '<span style="color:#06c;">'.$__('On-site').'</span>'
                                : $__('Office')).'</td>';
            case 'trips':   return '<td style="text-align:center;">'.(int) ($r['trips'] ?? 0).'</td>';
            case 'travel':  return '<td style="text-align:center;">'.Billing::formatMoney($r['travel'] ?? 0, $config).'</td>';
            case 'rate':    return '<td style="text-align:center;">'
                                .(!empty($r['billable']) && $r['rate'] > 0
                                    ? Billing::formatMoney($r['rate'], $config) : '&mdash;').'</td>';
            case 'amount':  return '<td style="text-align:center;">'.Billing::formatMoney($r['amount'], $config).'</td>';
            case 'billed':  return '<td style="text-align:center;">'.(!empty($r['billed']) ? $__('Yes') : $__('No')).'</td>';
            case 'goodwill': return '<td style="text-align:center;">'.(!empty($r['is_goodwill']) ? $__('Yes') : '').'</td>';
            case 'closed':  return '<td style="text-align:center;">'.Format::htmlchars(Billing::formatDate($r['closed'] ?? '')).'</td>';
            case 'settled':
                if ($r['settled'] === '') return '<td></td>';
                return '<td style="text-align:center;">'.($r['settled'] === '1'
                                ? '<span style="color:#0a0;">'.$__('Settled').'</span>'
                                : '<span style="color:#c60;">'.$__('Open').'</span>').'</td>';
        }
        if (strpos($key, 'ff_') === 0)
            return '<td style="text-align:center;">'.Format::htmlchars(Billing::formValue($r[$key] ?? '')).'</td>';
        if (strpos($key, 'cd_') === 0 || strpos($key, 'core_') === 0)
            return '<td style="text-align:center;">'.Format::htmlchars((string)($r[$key] ?? '')).'</td>';
        return '<td></td>';
    }
}
if (!empty($canManage)) { ?>
<style type="text/css">
.billing-coledit > summary::-webkit-details-marker{display:none;}
.billing-coledit > summary::marker{content:"";}
</style>
<details class="billing-coledit"
         style="display:inline-block; position:relative; line-height:1;"
         <?php echo !empty($notice) ? 'open' : ''; ?>>
    <summary style="display:block; cursor:pointer; list-style:none;
                    line-height:1; font-size:16px; font-weight:normal; color:#3a9b35;"
             title="<?php echo $__('Customize columns'); ?>">
        <i class="icon-cog" style="display:block; line-height:1;"></i>
    </summary>
    <form action="<?php echo $colFormAction; ?>" method="post" class="no-pjax"
          style="position:absolute; left:0; top:1.8em; z-index:100; width:660px; text-align:left;
                 font-size:13px; font-weight:normal; color:#333; padding:10px;
                 background:#fff; border:1px solid #ccc; box-shadow:0 2px 8px rgba(0,0,0,.25);">
        <?php csrf_token(); ?>
        <input type="hidden" name="do" value="save_columns">
        <div style="overflow-y:auto; height:auto; max-height:350px;">
        <table class="table">
            <tbody>
                <tr class="header">
                    <td nowrap><small><b><?php echo $__('Heading and Width'); ?></b></small></td>
                    <td><small><b><?php echo $__('Column Details'); ?></b></small></td>
                    <td><small><b><?php echo $__('Sortable'); ?></b></small></td>
                </tr>
            </tbody>
            <tbody class="sortable-rows">
<?php   // the editor always edits the FULL stored set, not just this view's subset
        foreach ($fullColumns as $col) { $k = $col['key']; ?>
                <tr>
                    <td nowrap>
                        <i class="faded-more icon-sort"></i>
                        <input type="hidden" name="ckey[]" value="<?php echo Format::htmlchars($k); ?>">
                        <input type="text" size="25" name="clabel[]"
                               value="<?php echo Format::htmlchars($col['label']); ?>">
                        <input type="text" size="5" name="cwidth[]"
                               value="<?php echo (int) $col['width']; ?>">
                    </td>
                    <td><?php echo Format::htmlchars($catalog[$k]); ?></td>
                    <td>
                        <input type="checkbox" name="csort[<?php echo Format::htmlchars($k); ?>]"
                               value="1" <?php echo $col['sortable'] ? 'checked' : ''; ?>>
                        <a href="#" class="pull-right drop-column" title="<?php echo $__('Delete'); ?>"
                           onclick="var r=this.closest('tr'); r.parentNode.removeChild(r); return false;"><i class="icon-trash"></i></a>
                    </td>
                </tr>
<?php   } ?>
            </tbody>
            <tbody>
                <tr class="header"><td colspan="3"></td></tr>
                <tr>
                    <td colspan="3" id="append-column">
                        <i class="icon-plus-sign"></i>
                        <select name="add_key">
                            <option value="">&mdash; <?php echo $__('Add a column'); ?> &mdash;</option>
<?php   foreach ($addable as $k => $l)
            echo '<option value="'.Format::htmlchars($k).'">'.Format::htmlchars($l).'</option>'; ?>
                        </select>
                        <button type="submit" class="green button"><?php echo $__('Save'); ?></button>
                    </td>
                </tr>
            </tbody>
        </table>
        </div>
    </form>
</details>
<?php } ?>
