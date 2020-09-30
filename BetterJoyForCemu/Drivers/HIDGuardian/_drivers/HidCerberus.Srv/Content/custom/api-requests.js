/* Publish list of affected devices */
$.getJSON("/api/v1/hidguardian/affected/get", function (data) {
    var items = [];
    $.each(data, function (key, val) {
        items.push('<li class="list-group-item">'
            + val
            + '<span class="pull-right"><button type="button" class="affected-remove btn btn-primary btn-xs">Remove</button></span></li>');
    });

    if (items.length === 0) {
        $("#purge-affected-devices").hide();
    }

    $("<ul/>", {
        "class": "list-group",
        html: items.join("")
    }).prependTo("#affected-devices");
});

/* Publish list of whitelisted PIDs */
$.getJSON("/api/v1/hidguardian/whitelist/get", function (data) {
    var items = [];
    $.each(data, function (key, val) {
        items.push('<li class="list-group-item">'
            + val
            + '<span class="pull-right"><button type="button" class="whitelist-remove btn btn-primary btn-xs">Remove</button></span></li>');
    });

    if (items.length === 0) {
        $("#purge-whitelisted-pids").hide();
    }

    $("<ul/>", {
        "class": "list-group",
        html: items.join("")
    }).prependTo("#whitelisted-pids");
});

/* Submit forms via AJAX */
$(function () {
    $('#add-affected-devices').ajaxForm(function () {
        location.reload();
    });
});

/* Purge affected devices */
$(document).on("click", "#purge-affected-devices", function () {
    $.get("/api/v1/hidguardian/affected/purge", function (data, status) {
        location.reload();
    });
});

/* Purge whitelisted devices */
$(document).on("click", "#purge-whitelisted-pids", function () {
    $.get("/api/v1/hidguardian/whitelist/purge", function (data, status) {
        location.reload();
    });
});

/* Click handler for individual affected remove button  */
$(document).on("click", ".affected-remove", function () {
    $.post("/api/v1/hidguardian/affected/remove", {
        hwids: $(this).closest('li').clone().children().remove().end().text()
    })
        .done(function (data) {
            location.reload();
        });
});

/* Click handler for individual whitelist remove button  */
$(document).on("click", ".whitelist-remove", function () {
    $.get("/api/v1/hidguardian/whitelist/remove/"
        + $(this).closest('li').clone().children().remove().end().text(), function (data, status) {
            location.reload();
        });
});

/* Click handler for modal dialog close button  */
$(document).on("click", ".hide-dialog-close", function () {
    location.reload();
});
