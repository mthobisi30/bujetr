// Toggles commitment-rule fields based on the selected rule type and shows a hint.
(function () {
    var select = document.getElementById('ruleTypeSelect');
    if (!select) return;

    var help = {
        SpendingThreshold: 'Triggers when a single transaction exceeds the threshold amount.',
        CategoryLimit: 'Triggers when a transaction in the chosen category exceeds the threshold.',
        TimeBasedLimit: 'Triggers when spending exceeds the threshold during the chosen days/hours.',
        MerchantBlock: 'Triggers when the merchant name contains your keyword.'
    };

    function show(id, on) {
        var el = document.getElementById(id);
        if (el) el.style.display = on ? '' : 'none';
    }

    function refresh() {
        var t = select.value;
        show('thresholdFields', ['SpendingThreshold', 'CategoryLimit', 'TimeBasedLimit'].indexOf(t) >= 0);
        show('categoryField', t === 'CategoryLimit');
        show('merchantField', t === 'MerchantBlock');
        show('timeFields', t === 'TimeBasedLimit');
        var h = document.getElementById('ruleTypeHelp');
        if (h) h.textContent = help[t] || '';
    }

    select.addEventListener('change', refresh);
    refresh();
})();
