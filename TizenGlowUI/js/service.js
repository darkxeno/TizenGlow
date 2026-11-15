const interval = setInterval(() => {
    try {
        tizen.application.getAppInfo('io.gh.darkxeno.TizenGlow');
        tizen.application.launch(
            'io.gh.darkxeno.TizenGlow',
            function () {
                console.log('Launch Service succeeded');
                clearInterval(interval);
            },
            function (e) {
                console.log("Launch Service failed: " + e.message);
            }
        );
    } catch (e) {
        console.log('App not found');
    }    
}, 1000);